﻿using System.Runtime.InteropServices;

namespace TS.NET
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ThunderscopeBridgeDataRegionHeader
    {
        // These structs are a snapshot that are true at the time the region is used by the reader
        public ThunderscopeHardwareConfig Hardware;           // 2 + 4*32, read only from UI perspective, UI uses SCPI interface to change configuration
        public ThunderscopeProcessingConfig Processing;       // 37 bytes, read only from UI perspective, UI uses SCPI interface to change configuration
        public bool Triggered;                                // Indicate if acquired data was triggered (i.e. to run trigger interpolation or not)
    }
}