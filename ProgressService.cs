using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

namespace AuroraLauncher
{
    internal static class ProgressService
    {
        private static readonly object Gate = new();
        private static readonly List<JsonObject> Groups = new();
        private static readonly AsyncLocal<string?> CurrentGroup = new();
        private static readonly AsyncLocal<string?> CurrentStep = new();
        private static readonly AsyncLocal<bool> PackScope = new();

        public static bool IsActive
        {
            get
            {
                lock (Gate)
                {
                    var id = CurrentGroup.Value;
                    if (id == null) return false;
                    foreach (var group in Groups)
                        if (group["id"]?.GetValue<string>() == id)
                            return group["status"]?.GetValue<string>() == "running";
                    return false;
                }
            }
        }

        public static bool Begin(string title)
        {
            // Nested installers inherit the pack scope and must reuse its card.
            if (PackScope.Value) return false;
            var group = NewGroup(title);
            CurrentGroup.Value = group["id"]?.GetValue<string>();
            CurrentStep.Value = null;
            Emit();
            return true;
        }

        public static bool BeginPack(string title)
        {
            PackScope.Value = true;
            var group = NewGroup(title);
            CurrentGroup.Value = group["id"]?.GetValue<string>();
            CurrentStep.Value = null;
            Emit();
            return true;
        }

        public static void CompletePack()
        {
            PackScope.Value = false;
            Complete();
        }

        public static string Step(string title, double total = 1, string detail = "")
        {
            var id = Guid.NewGuid().ToString("N")[..10];
            lock (Gate)
            {
                var group = ActiveGroup();
                if (group == null) return "";
                var steps = group["steps"] as JsonArray;
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
                    ["id"] = id,
                    ["title"] = title,
                    ["detail"] = detail,
                    ["current"] = 0,
                    ["total"] = total,
                    ["status"] = "running"
                });
                CurrentStep.Value = id;
            }
            Emit();
            return id;
        }

        public static void UpdateAt(string stepId, double current, string detail = "")
            => MutateAt(stepId, step => { step["current"] = current; step["detail"] = detail; step["status"] = "done"; });

        public static void ProgressAt(string stepId, double current, string detail = "")
            => MutateAt(stepId, step => { step["current"] = current; step["detail"] = detail; });

        public static void TotalAt(string stepId, double total)
            => MutateAt(stepId, step => step["total"] = total);

        public static void Update(double current, string detail = "")
            => MutateCurrent(step => { step["current"] = current; step["detail"] = detail; step["status"] = "done"; });

        public static void Total(double total) => MutateCurrent(step => step["total"] = total);

        public static void Progress(double current, string detail = "")
            => MutateCurrent(step => { step["current"] = current; step["detail"] = detail; });

        public static void Detail(string detail) => MutateCurrent(step => step["detail"] = detail);

        public static void Complete()
        {
            if (PackScope.Value) return;
            lock (Gate)
            {
                var group = ActiveGroup();
                if (group == null) return;
                if (group["steps"] is JsonArray steps)
                {
                    for (var i = 0; i < steps.Count; i++)
                        if (steps[i] is JsonObject step) step["status"] = "done";
                }
                group["status"] = "done";
                group["finishedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            Emit();
        }

        public static void Fail(string error)
        {
            PackScope.Value = false;
            lock (Gate)
            {
                var group = ActiveGroup();
                if (group == null) return;
                if (group["steps"] is JsonArray steps)
                {
                    for (var i = 0; i < steps.Count; i++)
                        if (steps[i] is JsonObject step && step["status"]?.GetValue<string>() == "running") step["status"] = "error";
                }
                group["status"] = "error";
                group["error"] = error;
                group["finishedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            Emit();
        }

        private static JsonObject NewGroup(string title)
        {
            lock (Gate)
            {
                var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var group = new JsonObject
                {
                    ["id"] = "install-" + Guid.NewGuid().ToString("N")[..10],
                    ["title"] = title,
                    ["steps"] = new JsonArray(),
                    ["status"] = "running",
                    ["startedAt"] = startedAt
                };
                Groups.Add(group);
                if (Groups.Count > 20)
                {
                    var finished = Groups.Where(item => item["status"]?.GetValue<string>() != "running")
                        .OrderBy(item => item["startedAt"]?.GetValue<long>() ?? 0).ToList();
                    foreach (var item in finished.Take(Math.Max(0, Groups.Count - 20))) Groups.Remove(item);
                }
                return group;
            }
        }

        private static JsonObject? ActiveGroup()
        {
            var id = CurrentGroup.Value;
            if (id != null)
            {
                foreach (var group in Groups)
                    if (group["id"]?.GetValue<string>() == id) return group;
            }
            // Compatibility for calls that were fired from a callback without a
            // captured context: fall back to the newest running group.
            return Groups.LastOrDefault(group => group["status"]?.GetValue<string>() == "running") ?? Groups.LastOrDefault();
        }

        private static void MutateAt(string stepId, Action<JsonObject> action)
        {
            lock (Gate)
            {
                var group = ActiveGroup();
                if (group?["steps"] is not JsonArray steps) return;
                for (var i = 0; i < steps.Count; i++)
                {
                    if (steps[i] is not JsonObject step || step["id"]?.GetValue<string>() != stepId) continue;
                    action(step);
                    break;
                }
            }
            Emit();
        }

        private static void MutateCurrent(Action<JsonObject> action)
        {
            lock (Gate)
            {
                var group = ActiveGroup();
                if (group?["steps"] is not JsonArray steps) return;
                var id = CurrentStep.Value;
                for (var i = 0; i < steps.Count; i++)
                {
                    if (steps[i] is not JsonObject step || step["id"]?.GetValue<string>() != id) continue;
                    action(step);
                    break;
                }
            }
            Emit();
        }

        private static void Emit()
        {
            JsonObject snapshot;
            lock (Gate)
            {
                if (Groups.Count == 0) return;
                var groups = new JsonArray();
                foreach (var group in Groups) groups.Add(group.DeepCloneCompat());
                snapshot = new JsonObject
                {
                    ["groups"] = groups,
                    ["activeGroupId"] = CurrentGroup.Value ?? Groups.LastOrDefault()?["id"]?.GetValue<string>()
                };
            }
            GameEvents.Emit("progress", snapshot);
        }
    }
}
