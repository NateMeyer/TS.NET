﻿using System.Runtime.InteropServices;

namespace TS.NET
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]     // Struct packing allows the use of this datatype in ThunderscopeBridge header as it's consistent with the other datatypes
    public struct ThunderscopeHealthStatus
    {
        public uint AdcSampleRate;
        public uint AdcSampleSize;
        public uint AdcSampleResolution;
        public uint AdcSamplesLost;
        public double FpgaTemp;
        public double VccInt;
        public double VccAux;
        public double VccBram;

        public static ThunderscopeHealthStatus Default()
        {
            return new ThunderscopeHealthStatus();
        }
    }

}
