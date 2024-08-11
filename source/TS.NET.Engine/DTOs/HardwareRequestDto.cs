﻿using System;

namespace TS.NET.Engine
{
    public abstract record HardwareRequestDto();
    public record HardwareStartRequest() : HardwareRequestDto;
    public record HardwareStopRequest() : HardwareRequestDto;

    public abstract record HardwareSetChannelFrontendRequest(int ChannelIndex) : HardwareRequestDto;
    public record HardwareSetEnabledRequest(int ChannelIndex, bool Enabled) : HardwareSetChannelFrontendRequest(ChannelIndex);
    public record HardwareSetVoltOffsetRequest(int ChannelIndex, double VoltOffset) : HardwareSetChannelFrontendRequest(ChannelIndex);
    public record HardwareSetVoltFullScaleRequest(int ChannelIndex, double VoltFullScale) : HardwareSetChannelFrontendRequest(ChannelIndex);
    public record HardwareSetBandwidthRequest(int ChannelIndex, ThunderscopeBandwidth Bandwidth) : HardwareSetChannelFrontendRequest(ChannelIndex);
    public record HardwareSetCouplingRequest(int ChannelIndex, ThunderscopeCoupling Coupling) : HardwareSetChannelFrontendRequest(ChannelIndex);
    public record HardwareSetTerminationRequest(int ChannelIndex, ThunderscopeTermination Termination) : HardwareSetChannelFrontendRequest(ChannelIndex);

    public abstract record HardwareSetChannelFrontendOverrideRequest(int ChannelIndex) : HardwareRequestDto;
    public record HardwareSetPgaConfigWordOverrideRequest(int ChannelIndex, ushort PgaConfigWord) : HardwareSetChannelFrontendOverrideRequest(ChannelIndex);

    public abstract record HardwareSetChannelCalibrationRequest(int ChannelIndex) : HardwareRequestDto;
    public record HardwareSetOffsetVoltageLowGainRequest(int ChannelIndex, double OffsetVoltage) : HardwareSetChannelCalibrationRequest(ChannelIndex);
    public record HardwareSetOffsetVoltageHighGainRequest(int ChannelIndex, double OffsetVoltage) : HardwareSetChannelCalibrationRequest(ChannelIndex);
}