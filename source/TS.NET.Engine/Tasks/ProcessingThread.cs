﻿using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace TS.NET.Engine
{
    public class ProcessingThread
    {
        private readonly ILogger logger;
        private readonly ThunderscopeSettings settings;
        private readonly BlockingChannelReader<InputDataDto> processChannel;
        private readonly BlockingChannelWriter<ThunderscopeMemory> inputChannel;
        private readonly BlockingChannelReader<ProcessingRequestDto> processingRequestChannel;
        private readonly BlockingChannelWriter<ProcessingResponseDto> processingResponseChannel;

        private CancellationTokenSource? cancelTokenSource;
        private Task? taskLoop;

        public ProcessingThread(
            ILoggerFactory loggerFactory,
            ThunderscopeSettings settings,
            BlockingChannelReader<InputDataDto> processChannel,
            BlockingChannelWriter<ThunderscopeMemory> inputChannel,
            BlockingChannelReader<ProcessingRequestDto> processingRequestChannel,
            BlockingChannelWriter<ProcessingResponseDto> processingResponseChannel)
        {
            logger = loggerFactory.CreateLogger(nameof(ProcessingThread));
            this.settings = settings;
            this.processChannel = processChannel;
            this.inputChannel = inputChannel;
            this.processingRequestChannel = processingRequestChannel;
            this.processingResponseChannel = processingResponseChannel;
        }

        public void Start()
        {
            cancelTokenSource = new CancellationTokenSource();
            taskLoop = Task.Factory.StartNew(() => Loop(logger, settings, processChannel, inputChannel, processingRequestChannel, processingResponseChannel, cancelTokenSource.Token), TaskCreationOptions.LongRunning);
        }

        public void Stop()
        {
            cancelTokenSource?.Cancel();
            taskLoop?.Wait();
        }

        // The job of this task - pull data from scope driver/simulator, shuffle if 2/4 channels, horizontal sum, trigger, and produce window segments.
        private static void Loop(
            ILogger logger,
            ThunderscopeSettings settings,
            BlockingChannelReader<InputDataDto> processChannel,
            BlockingChannelWriter<ThunderscopeMemory> inputChannel,
            BlockingChannelReader<ProcessingRequestDto> processingRequestChannel,
            BlockingChannelWriter<ProcessingResponseDto> processingResponseChannel,
            CancellationToken cancelToken)
        {
            try
            {
                Thread.CurrentThread.Name = nameof(ProcessingThread);
                if (settings.ProcessingThreadProcessorAffinity > -1 && OperatingSystem.IsWindows())
                {
                    Thread.BeginThreadAffinity();
                    Interop.CurrentThread.ProcessorAffinity = new IntPtr(1 << settings.ProcessingThreadProcessorAffinity);
                    logger.LogDebug($"{nameof(ProcessingThread)} thread processor affinity set to {settings.ProcessingThreadProcessorAffinity}");
                }

                ThunderscopeDataBridgeConfig bridgeConfig = new()
                {
                    MaxChannelCount = settings.MaxChannelCount,
                    MaxChannelDataLength = settings.MaxChannelDataLength,
                    ChannelDataType = ThunderscopeChannelDataType.I8
                };
                ThunderscopeDataBridgeWriter bridge = new("ThunderScope.1", bridgeConfig);

                ThunderscopeHardwareConfig cachedThunderscopeConfiguration = default;

                // Set some sensible defaults
                var processingConfig = new ThunderscopeProcessingConfig
                {
                    CurrentChannelCount = settings.MaxChannelCount,
                    CurrentChannelDataLength = settings.MaxChannelDataLength,
                    HorizontalSumLength = HorizontalSumLength.None,
                    TriggerChannel = TriggerChannel.One,
                    TriggerMode = TriggerMode.Auto,
                    TriggerType = TriggerType.RisingEdge
                };
                bridge.Processing = processingConfig;

                // Reset monitoring
                bridge.MonitoringReset();

                // Various buffers allocated once and reused forevermore.
                //Memory<byte> hardwareBuffer = new byte[ThunderscopeMemory.Length];
                // Shuffle buffers. Only needed for 2/4 channel modes.
                Span<sbyte> shuffleBuffer = new sbyte[ThunderscopeMemory.Length];
                // --2 channel buffers
                int blockLength_2 = (int)ThunderscopeMemory.Length / 2;
                Span<sbyte> postShuffleCh1_2 = shuffleBuffer.Slice(0, blockLength_2);
                Span<sbyte> postShuffleCh2_2 = shuffleBuffer.Slice(blockLength_2, blockLength_2);
                // --4 channel buffers
                int blockLength_4 = (int)ThunderscopeMemory.Length / 4;
                Span<sbyte> postShuffleCh1_4 = shuffleBuffer.Slice(0, blockLength_4);
                Span<sbyte> postShuffleCh2_4 = shuffleBuffer.Slice(blockLength_4, blockLength_4);
                Span<sbyte> postShuffleCh3_4 = shuffleBuffer.Slice(blockLength_4 * 2, blockLength_4);
                Span<sbyte> postShuffleCh4_4 = shuffleBuffer.Slice(blockLength_4 * 3, blockLength_4);

                uint triggerHysteresis = 10;
                Span<uint> triggerIndices = new uint[ThunderscopeMemory.Length / 1000];     // 1000 samples is the minimum holdoff
                Span<uint> holdoffEndIndices = new uint[ThunderscopeMemory.Length / 1000];  // 1000 samples is the minimum holdoff
                // By setting holdoffSamples to processingConfig.CurrentChannelDataLength, the holdoff is the exact length of the data sent over the bridge which gives near gapless triggering
                RisingEdgeTriggerI8 risingEdgeTrigger = new(0, (sbyte)-triggerHysteresis, processingConfig.CurrentChannelDataLength);
                FallingEdgeTriggerI8 fallingEdgeTrigger = new(0, (sbyte)triggerHysteresis, processingConfig.CurrentChannelDataLength);

                DateTimeOffset startTime = DateTimeOffset.UtcNow;
                uint dequeueCounter = 0;
                uint oneSecondHoldoffCount = 0;
                uint oneSecondDequeueCount = 0;
                // HorizontalSumUtility.ToDivisor(horizontalSumLength)
                Stopwatch periodicUpdateTimer = Stopwatch.StartNew();

                var circularBuffer1 = new ChannelCircularAlignedBufferI8((uint)processingConfig.CurrentChannelDataLength + ThunderscopeMemory.Length);
                var circularBuffer2 = new ChannelCircularAlignedBufferI8((uint)processingConfig.CurrentChannelDataLength + ThunderscopeMemory.Length);
                var circularBuffer3 = new ChannelCircularAlignedBufferI8((uint)processingConfig.CurrentChannelDataLength + ThunderscopeMemory.Length);
                var circularBuffer4 = new ChannelCircularAlignedBufferI8((uint)processingConfig.CurrentChannelDataLength + ThunderscopeMemory.Length);

                // Triggering:
                // There are 3 states for Trigger Mode: normal, single, auto.
                // (these only run during Start, not during Stop. Invoking Force will ignore Start/Stop.)
                // Normal: wait for trigger indefinately and run continuously.
                // Single: wait for trigger indefinately and then stop.
                // Auto: wait for trigger indefinately, push update when timeout occurs, and run continously.
                //
                // runTrigger: enables/disables trigger subsystem. 
                // forceTriggerLatch: disregards the Trigger Mode, push update immediately and set forceTrigger to false. If a standard trigger happened at the same time as a force, the force is ignored so the bridge only updates once.
                // singleTriggerLatch: used in Single mode to stop the trigger subsystem after a trigger.

                bool runTrigger = true;
                bool forceTriggerLatch = false;     // "Latch" because it will reset state back to false automatically. If the force is invoked and a trigger happens anyway, it will be reset (effectively ignoring it and only updating the bridge once).
                bool singleTriggerLatch = false;    // "Latch" because it will reset state back to false automatically. When reset, runTrigger will be set to false.
                Stopwatch autoTimer = Stopwatch.StartNew();

                logger.LogInformation("Started");

                while (true)
                {
                    cancelToken.ThrowIfCancellationRequested();

                    // Check for processing requests
                    if (processingRequestChannel.TryRead(out var request))
                    {
                        switch (request)
                        {
                            case ProcessingRunDto processingStartTriggerDto:
                                runTrigger = true;
                                logger.LogDebug($"Set Run");
                                break;
                            case ProcessingStopDto processingStopTriggerDto:
                                runTrigger = false;
                                logger.LogDebug($"Set Stop");
                                break;
                            case ProcessingForceTriggerDto processingForceTriggerDto:
                                forceTriggerLatch = true;
                                logger.LogDebug($"Set Force");
                                break;
                            case ProcessingSetTriggerModeDto processingSetTriggerModeDto:
                                processingConfig.TriggerMode = processingSetTriggerModeDto.Mode;
                                switch (processingSetTriggerModeDto.Mode)
                                {
                                    case TriggerMode.Normal:
                                        singleTriggerLatch = false;
                                        break;
                                    case TriggerMode.Single:
                                        singleTriggerLatch = true;
                                        break;
                                    case TriggerMode.Auto:
                                        autoTimer.Restart();
                                        singleTriggerLatch = false;
                                        break;
                                }
                                logger.LogDebug($"Set TriggerMode to {processingConfig.TriggerMode}");
                                break;
                            case ProcessingSetDepthDto processingSetDepthDto:
                                var depth = processingSetDepthDto.Samples;
                                processingConfig.CurrentChannelDataLength = depth;
                                risingEdgeTrigger.SetHoldoff(depth);
                                fallingEdgeTrigger.SetHoldoff(depth);
                                logger.LogDebug($"Set CurrentChannelDataLength to {processingConfig.CurrentChannelDataLength}");
                                break;
                            case ProcessingSetRateDto processingSetRateDto:
                                var rate = processingSetRateDto.SamplingHz;
                                logger.LogWarning($"{nameof(ProcessingSetRateDto)} not implemented");
                                break;
                            case ProcessingSetTriggerSourceDto processingSetTriggerSourceDto:
                                var channel = processingSetTriggerSourceDto.Channel;
                                processingConfig.TriggerChannel = channel;
                                logger.LogDebug($"Set TriggerChannel to {processingConfig.TriggerChannel}");
                                break;
                            case ProcessingSetTriggerDelayDto processingSetTriggerDelayDto:
                                var fs = processingSetTriggerDelayDto.Femtoseconds;
                                logger.LogWarning($"{nameof(ProcessingSetTriggerDelayDto)} not implemented");
                                break;
                            case ProcessingSetTriggerLevelDto processingSetTriggerLevelDto:
                                var requestedTriggerLevel = processingSetTriggerLevelDto.LevelVolts;
                                // Convert the voltage to Int8

                                var triggerChannel = cachedThunderscopeConfiguration.GetTriggerChannel(processingConfig.TriggerChannel);

                                if ((requestedTriggerLevel > triggerChannel.ActualVoltFullScale / 2) || (requestedTriggerLevel < -triggerChannel.ActualVoltFullScale / 2))
                                {
                                    logger.LogWarning($"Could not set trigger level {requestedTriggerLevel}");
                                    break;
                                }

                                sbyte triggerLevel = (sbyte)((requestedTriggerLevel / (triggerChannel.ActualVoltFullScale / 2)) * 127f);
                                switch(processingConfig.TriggerType)
                                {
                                    case TriggerType.RisingEdge:
                                        if (triggerLevel == sbyte.MinValue)
                                            triggerLevel += (sbyte)triggerHysteresis;   // Coerce so that the trigger arm level is sbyte.MinValue, ensuring a non-zero chance of seeing some waveforms
                                        if (triggerLevel == sbyte.MaxValue)
                                            triggerLevel -= 1;                          // Coerce as the trigger logic is GT, ensuring a non-zero chance of seeing some waveforms
                                        break;
                                    case TriggerType.FallingEdge:
                                        if (triggerLevel == sbyte.MaxValue)
                                            triggerLevel -= (sbyte)triggerHysteresis;   // Coerce so that the trigger arm level is sbyte.MinValue, ensuring a non-zero chance of seeing some waveforms
                                        if (triggerLevel == sbyte.MinValue)
                                            triggerLevel += 1;                          // Coerce as the trigger logic is LT, ensuring a non-zero chance of seeing some waveforms
                                        break;
                                }
                               
                                risingEdgeTrigger.Reset(triggerLevel, triggerLevel -= (sbyte)triggerHysteresis, processingConfig.CurrentChannelDataLength);
                                fallingEdgeTrigger.Reset(triggerLevel, triggerLevel += (sbyte)triggerHysteresis, processingConfig.CurrentChannelDataLength);
                                logger.LogDebug($"Set trigger level to {triggerLevel} with hysteresis of {triggerHysteresis}");
                                break;
                            case ProcessingSetTriggerTypeDto processingSetTriggerTypeDto:
                                processingConfig.TriggerType = processingSetTriggerTypeDto.Type;
                                logger.LogDebug($"Set TriggerType to {processingConfig.TriggerType}");
                                break;
                            default:
                                logger.LogWarning($"Unknown ProcessingRequestDto: {request}");
                                break;
                        }

                        bridge.Processing = processingConfig;
                    }

                    InputDataDto inputDataDto = processChannel.Read(cancelToken);
                    cachedThunderscopeConfiguration = inputDataDto.HardwareConfig;
                    bridge.Hardware = inputDataDto.HardwareConfig;
                    dequeueCounter++;
                    oneSecondDequeueCount++;

                    int channelLength = (int)processingConfig.CurrentChannelDataLength;
                    switch (inputDataDto.HardwareConfig.AdcChannelMode)
                    {
                        // Processing pipeline:
                        // Shuffle (if needed)
                        // Horizontal sum (EDIT: triggering should happen _before_ horizontal sum)
                        // Write to circular buffer
                        // Trigger
                        // Data segment on trigger (if needed)
                        case AdcChannelMode.Single:
                            throw new NotImplementedException();
                            // Horizontal sum (EDIT: triggering should happen _before_ horizontal sum)
                            //if (config.HorizontalSumLength != HorizontalSumLength.None)
                            //    throw new NotImplementedException();
                            // Write to circular buffer
                            circularBuffer1.Write(inputDataDto.Memory.SpanI8);
                            // Trigger
                            if (processingConfig.TriggerChannel != TriggerChannel.None)
                            {
                                var triggerChannelBuffer = processingConfig.TriggerChannel switch
                                {
                                    TriggerChannel.One => inputDataDto.Memory.SpanI8,
                                    _ => throw new ArgumentException("Invalid TriggerChannel value")
                                };
                                risingEdgeTrigger.ProcessSimd(input: triggerChannelBuffer, triggerIndices: triggerIndices, out uint triggerCount, holdoffEndIndices: holdoffEndIndices, out uint holdoffEndCount);
                            }
                            // Finished with the memory, return it
                            inputChannel.Write(inputDataDto.Memory);
                            break;
                        case AdcChannelMode.Dual:
                            throw new NotImplementedException();
                            // Shuffle
                            Shuffle.TwoChannels(input: inputDataDto.Memory.SpanI8, output: shuffleBuffer);
                            // Finished with the memory, return it
                            inputChannel.Write(inputDataDto.Memory);
                            // Horizontal sum (EDIT: triggering should happen _before_ horizontal sum)
                            //if (config.HorizontalSumLength != HorizontalSumLength.None)
                            //    throw new NotImplementedException();
                            // Write to circular buffer
                            circularBuffer1.Write(postShuffleCh1_2);
                            circularBuffer2.Write(postShuffleCh2_2);
                            // Trigger
                            if (processingConfig.TriggerChannel != TriggerChannel.None)
                            {
                                var triggerChannelBuffer = processingConfig.TriggerChannel switch
                                {
                                    TriggerChannel.One => postShuffleCh1_2,
                                    TriggerChannel.Two => postShuffleCh2_2,
                                    _ => throw new ArgumentException("Invalid TriggerChannel value")
                                };
                                risingEdgeTrigger.ProcessSimd(input: triggerChannelBuffer, triggerIndices: triggerIndices, out uint triggerCount, holdoffEndIndices: holdoffEndIndices, out uint holdoffEndCount);
                            }
                            break;
                        case AdcChannelMode.Quad:
                            // Shuffle
                            Shuffle.FourChannels(input: inputDataDto.Memory.SpanI8, output: shuffleBuffer);
                            // Finished with the memory, return it
                            inputChannel.Write(inputDataDto.Memory);
                            // Horizontal sum (EDIT: triggering should happen _before_ horizontal sum)
                            //if (config.HorizontalSumLength != HorizontalSumLength.None)
                            //    throw new NotImplementedException();
                            // Write to circular buffer
                            circularBuffer1.Write(postShuffleCh1_4);
                            circularBuffer2.Write(postShuffleCh2_4);
                            circularBuffer3.Write(postShuffleCh3_4);
                            circularBuffer4.Write(postShuffleCh4_4);
                            // Trigger
                            if (runTrigger && processingConfig.TriggerChannel != TriggerChannel.None)
                            {
                                var triggerChannelBuffer = processingConfig.TriggerChannel switch
                                {
                                    TriggerChannel.One => postShuffleCh1_4,
                                    TriggerChannel.Two => postShuffleCh2_4,
                                    TriggerChannel.Three => postShuffleCh3_4,
                                    TriggerChannel.Four => postShuffleCh4_4,
                                    _ => throw new ArgumentException("Invalid TriggerChannel value")
                                };

                                uint triggerCount = 0;
                                uint holdoffEndCount = 0;
                                switch(processingConfig.TriggerType)
                                {
                                    case TriggerType.RisingEdge:
                                    {
                                        risingEdgeTrigger.ProcessSimd(input: triggerChannelBuffer, triggerIndices: triggerIndices, out triggerCount, holdoffEndIndices: holdoffEndIndices, out holdoffEndCount);
                                        break;
                                    }
                                        
                                    case TriggerType.FallingEdge:
                                    {
                                        fallingEdgeTrigger.ProcessSimd(input: triggerChannelBuffer, triggerIndices: triggerIndices, out triggerCount, holdoffEndIndices: holdoffEndIndices, out holdoffEndCount);  
                                        break;
                                    }
                                }
                                
                                oneSecondHoldoffCount += holdoffEndCount;
                                if (holdoffEndCount > 0)
                                {
                                    //logger.LogDebug("Trigger fired");
                                    for (int i = 0; i < holdoffEndCount; i++)
                                    {
                                        var bridgeSpan = bridge.AcquiringRegionI8;
                                        uint holdoffEndIndex = (uint)postShuffleCh1_4.Length - holdoffEndIndices[i];
                                        circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), holdoffEndIndex);
                                        circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), holdoffEndIndex);
                                        circularBuffer3.Read(bridgeSpan.Slice(channelLength + channelLength, channelLength), holdoffEndIndex);
                                        circularBuffer4.Read(bridgeSpan.Slice(channelLength + channelLength + channelLength, channelLength), holdoffEndIndex);
                                        bridge.DataWritten();
                                        bridge.SwitchRegionIfNeeded();
                                    }
                                    forceTriggerLatch = false;      // Ignore the force trigger request, if any, as a non-force trigger happened
                                    autoTimer.Restart();            // Restart the timer so there aren't auto updates if regular triggering is happening.
                                    if (singleTriggerLatch)         // If this was a single trigger, reset the singleTrigger & runTrigger latches
                                    {
                                        singleTriggerLatch = false;
                                        runTrigger = false;
                                    }
                                }
                                else if (processingConfig.TriggerMode == TriggerMode.Auto && autoTimer.ElapsedMilliseconds > 1000)
                                {
                                    oneSecondHoldoffCount++;
                                    //logger.LogDebug("Auto trigger fired");
                                    var bridgeSpan = bridge.AcquiringRegionI8;
                                    circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), 0);
                                    circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), 0);
                                    circularBuffer3.Read(bridgeSpan.Slice(channelLength + channelLength, channelLength), 0);
                                    circularBuffer4.Read(bridgeSpan.Slice(channelLength + channelLength + channelLength, channelLength), 0);
                                    bridge.DataWritten();
                                    bridge.SwitchRegionIfNeeded();
                                    forceTriggerLatch = false;      // Ignore the force trigger request, if any, as a non-force trigger happened
                                    autoTimer.Restart();            // Restart the timer so we get auto updates at a regular interval
                                }
                                else
                                {
                                    bridge.SwitchRegionIfNeeded();  // To do: add a comment here when the reason for this LoC is discovered...!
                                }

                            }
                            if (forceTriggerLatch)             // If a forceTriggerLatch is still active, send data to the bridge and reset latch.
                            {
                                oneSecondHoldoffCount++;
                                //logger.LogDebug("Force trigger fired");
                                var bridgeSpan = bridge.AcquiringRegionI8;
                                circularBuffer1.Read(bridgeSpan.Slice(0, channelLength), 0);
                                circularBuffer2.Read(bridgeSpan.Slice(channelLength, channelLength), 0);
                                circularBuffer3.Read(bridgeSpan.Slice(channelLength + channelLength, channelLength), 0);
                                circularBuffer4.Read(bridgeSpan.Slice(channelLength + channelLength + channelLength, channelLength), 0);
                                bridge.DataWritten();
                                bridge.SwitchRegionIfNeeded();
                                forceTriggerLatch = false;
                            }

                            //logger.LogInformation($"Dequeue #{dequeueCounter++}, Ch1 triggers: {triggerCount1}, Ch2 triggers: {triggerCount2}, Ch3 triggers: {triggerCount3}, Ch4 triggers: {triggerCount4} ");
                            break;
                    }

                    if (periodicUpdateTimer.ElapsedMilliseconds >= 10000)
                    {
                        logger.LogDebug($"Outstanding frames: {processChannel.PeekAvailable()}, dequeues/sec: {oneSecondDequeueCount / (periodicUpdateTimer.Elapsed.TotalSeconds):F2}, dequeue count: {dequeueCounter}");
                        logger.LogDebug($"Triggers/sec: {oneSecondHoldoffCount / (periodicUpdateTimer.Elapsed.TotalSeconds):F2}, trigger count: {bridge.Monitoring.TotalAcquisitions}, UI dropped triggers: {bridge.Monitoring.MissedAcquisitions}");
                        periodicUpdateTimer.Restart();
                        oneSecondHoldoffCount = 0;
                        oneSecondDequeueCount = 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("Stopping...");
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Error");
                throw;
            }
            finally
            {
                logger.LogDebug("Stopped");
            }
        }
    }
}
