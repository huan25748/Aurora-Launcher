using System;
namespace AuroraLauncher
{
    internal static class VersionInfo
    {
        public static string Format(Version? version)
        {
            if (version == null) return "0.0.0";
            return version.Revision > 0 ? version.ToString(4) : version.ToString(3);
        }
    }
}