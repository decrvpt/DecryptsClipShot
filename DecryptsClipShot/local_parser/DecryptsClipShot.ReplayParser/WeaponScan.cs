using System.Collections;
using System.Diagnostics;
using System.Reflection;

namespace DecryptsClipShot.ReplayParser;

/// <summary>
/// Diagnostic: lists every weapon-looking name anywhere in the parsed replay
/// (item definitions like "WID_Sniper_Heavy_Athena_SR_Ore_T03", weapon paths,
/// rarity words) with where it was found, so the exact weapon and its rarity
/// can be traced to a kill. Writes weapons_found.txt next to the result file.
/// Bounded in time and size; any failure is ignored.
/// </summary>
internal static class WeaponScan
{
    public static void Write(object replay, string path)
    {
        try
        {
            var found = new Dictionary<string, int>(StringComparer.Ordinal);
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(object Value, string Where, int Depth)>();
            stack.Push((replay, "replay", 0));
            var clock = Stopwatch.StartNew();
            int budget = 400_000;
            while (stack.Count > 0 && budget-- > 0 && clock.ElapsedMilliseconds < 5000)
            {
                (object value, string where, int depth) = stack.Pop();
                if (value is string text)
                {
                    if (Interesting(text))
                    {
                        string key = $"{text}   @ {where}";
                        found[key] = found.GetValueOrDefault(key) + 1;
                    }
                    continue;
                }
                Type type = value.GetType();
                if (type.IsPrimitive || type.IsEnum || value is decimal or DateTime or TimeSpan or Guid) continue;
                if (!type.IsValueType && !visited.Add(value)) continue;
                if (depth > 9) continue;
                if (value is IDictionary dictionary)
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entry.Key is not null) stack.Push((entry.Key, where + "{}", depth + 1));
                        if (entry.Value is not null) stack.Push((entry.Value, where + "[]", depth + 1));
                    }
                    continue;
                }
                if (value is IEnumerable items)
                {
                    int count = 0;
                    foreach (object? item in items)
                    {
                        if (item is null) continue;
                        if (++count > 20000) break;
                        stack.Push((item, where + "[]", depth + 1));
                    }
                    continue;
                }
                if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) continue;
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0) continue;
                    object? child;
                    try { child = property.GetValue(value); }
                    catch { continue; }
                    if (child is not null) stack.Push((child, where + "." + property.Name, depth + 1));
                }
            }
            var lines = found.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(2000)
                .Select(pair => $"{pair.Value,5}x  {pair.Key}")
                .Prepend($"# Weapon-like names found in the replay ({found.Count}), scan took {clock.ElapsedMilliseconds} ms");
            File.WriteAllLines(path, lines);
        }
        catch
        {
            // Diagnostic only.
        }
    }

    private static bool Interesting(string text) =>
        text.Length < 400 &&
        (text.Contains("WID_", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("/Weapons/", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("Rarity", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("Sniper", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("ItemDefinition", StringComparison.OrdinalIgnoreCase));
}
