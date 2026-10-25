using System;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class ProgressService
    {
        private static readonly object Gate = new();
        private static JsonObject? _group;
        private static string _stepId = "";
        private static long _startedAt;

        public static bool IsActive
        {
            get
            {
                lock (Gate) return _group != null && _group["status"]?.GetValue<string>() == "running";
            }
        }

        public static void Begin(string title)
        {
            lock (Gate)
            {
                _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _group = new JsonObject
                {
                    ["id"] = "install-" + Guid.NewGuid().ToString("N")[..10],
                    ["title"] = title,
                    ["steps"] = new JsonArray(),
                    ["status"] = "running",
                    ["startedAt"] = _startedAt
                };
            }
            Emit();
        }

        public static string Step(string title, double total = 1, string detail = "")
        {
            var id = Guid.NewGuid().ToString("N")[..10];
            lock (Gate)
            {
                if (_group == null) return "";
                var steps = _group["steps"] as JsonArray;
                // A new step means the previous one has finished; otherwise the UI
                // can keep showing the old 100%-but-still-running step forever.
                if (steps is { Count: > 0 } && steps[^1] is JsonObject previous && previous["status"]?.GetValue<string>() == "running")
                {
                    previous["status"] = "done";
                    if (previous["current"] is JsonValue current && previous["total"] is JsonValue totalValue)
                    {
                        var currentNumber = current.TryGetValue<double>(out var c) ? c : 0;
                        var totalNumber = totalValue.TryGetValue<double>(out var t) ? t : 0;
                        if (totalNumber > 0 && currentNumber < totalNumber) previous["current"] = totalNumber;
                    }
                }
                steps?.Add(new JsonObject
                {
                    ["id"] = _stepId = id,
                    ["title"] = title,
                    ["detail"] = detail,
                    ["current"] = 0,
                    ["total"] = total,
                    ["status"] = "running"
                });
            }
            Emit();
            return id;
        }

        public static void UpdateAt(string stepId, double current, string detail = "")
        {
            lock (Gate)
            {
                if (_group?["steps"] is not JsonArray steps) return;
                for (var i = 0; i < steps.Count; i++)
                {
                    if (steps[i] is not JsonObject step || step["id"]?.GetValue<string>() != stepId) continue;
                    step["current"] = current;
                    step["detail"] = detail;
                    step["status"] = "done";
                    break;
                }
            }
            Emit();
        }

        public static void ProgressAt(string stepId, double current, string detail = "")
        {
            lock (Gate)
            {
                if (_group?["steps"] is not JsonArray steps) return;
                for (var i = 0; i < steps.Count; i++)
                {
                    if (steps[i] is not JsonObject step || step["id"]?.GetValue<string>() != stepId) continue;
                    step["current"] = current;
                    step["detail"] = detail;
                    break;
                }
            }
            Emit();
        }

        public static void TotalAt(string stepId, double total)
        {
            lock (Gate)
            {
                if (_group?["steps"] is not JsonArray steps) return;
                for (var i = 0; i < steps.Count; i++)
                    if (steps[i] is JsonObject step && step["id"]?.GetValue<string>() == stepId)
                        step["total"] = total;
            }
            Emit();
        }

        public static void Update(double current, string detail = "")
        {
            lock (Gate)
            {
                if (_group?["steps"] is not JsonArray steps) return;
                for (var i = 0; i < steps.Count; i++)
                {
                    if (steps[i] is not JsonObject step || step["id"]?.GetValue<string>() != _stepId) continue;
                    step["current"] = current;
                    step["detail"] = detail;
                    step["status"] = "done";
                    break;
                }
            }
            Emit();
        }

        public static void Total(double total)
        {
            lock (Gate)
            {
                if (_group?["steps"] is not JsonArray steps) return;
                for (var i = 0; i < steps.Count; i++)
                    if (steps[i] is JsonObject step && step["id"]?.GetValue<string>() == _stepId)
                        step["total"] = total;
            }
            Emit();
        }

        public static void Progress(double current, string detail = "")
        {
            lock (Gate)
            {
                if (_group?["steps"] is not JsonArray steps) return;
                for (var i = 0; i < steps.Count; i++)
                {
                    if (steps[i] is not JsonObject step || step["id"]?.GetValue<string>() != _stepId) continue;
                    step["current"] = current;
                    step["detail"] = detail;
                    break;
                }
            }
            Emit();
        }

        public static void Detail(string detail)
        {
            lock (Gate)
            {
                if (_group?["steps"] is not JsonArray steps) return;
                for (var i = 0; i < steps.Count; i++)
                    if (steps[i] is JsonObject step && step["id"]?.GetValue<string>() == _stepId)
                        step["detail"] = detail;
            }
            Emit();
        }

        public static void Complete()
        {
            lock (Gate)
            {
                if (_group == null) return;
                if (_group["steps"] is JsonArray steps)
                {
                    for (var i = 0; i < steps.Count; i++)
                        if (steps[i] is JsonObject step) step["status"] = "done";
                }
                _group["status"] = "done";
                _group["finishedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            Emit();
        }

        public static void Fail(string error)
        {
            lock (Gate)
            {
                if (_group == null) return;
                if (_group["steps"] is JsonArray steps)
                {
                    for (var i = 0; i < steps.Count; i++)
                        if (steps[i] is JsonObject step && step["status"]?.GetValue<string>() == "running") step["status"] = "error";
                }
                _group["status"] = "error";
                _group["error"] = error;
                _group["finishedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            Emit();
        }

        private static void Emit()
        {
            JsonObject snapshot;
            lock (Gate)
            {
                if (_group == null) return;
                snapshot = new JsonObject
                {
                    ["groups"] = new JsonArray(_group.DeepCloneCompat()),
                    ["activeGroupId"] = _group["id"]?.GetValue<string>()
                };
            }
            GameEvents.Emit("progress", snapshot);
        }
    }
}
