using System;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class GameEvents
    {
        public static event Action<string, JsonNode?>? Raised;

        public static void Emit(string name, JsonNode? payload)
        {
            Raised?.Invoke(name, payload);
        }
    }
}
