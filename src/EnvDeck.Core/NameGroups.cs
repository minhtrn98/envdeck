using System.Text.RegularExpressions;

namespace EnvDeck.Core;

/// <summary>Orders variables so that those with similar names (redis_url, redis_pw, acc_redis) sit side by side.</summary>
public static partial class NameGroups
{
    /// <summary>The parts of a name: split on "__", "_" and "-" (case-insensitive, empty parts dropped).</summary>
    public static string[] Parts(string name) =>
        SeparatorRun().Split(name.Trim().ToLowerInvariant()).Where(p => p.Length > 0).ToArray();

    /// <summary>
    /// How alike two names are: for every part they share, the smaller of the two parts' weights, where the part at
    /// index i weighs 1/(i+1). So a shared first part counts fully, a shared part further along counts less, and a
    /// part found at different positions counts as much as the later position allows:
    /// redis_url / redis_pw = 1, acc_redis / redis_url = 0.5, DEV_DB_USER / DEV_DB_PASSWORD = 1.5, JAVA_HOME / Path = 0.
    /// </summary>
    public static double Similarity(string a, string b)
    {
        var pa = Parts(a);
        var pb = Parts(b);
        var score = 0.0;
        for (var i = 0; i < pa.Length; i++)
        {
            if (Array.IndexOf(pa, pa[i]) != i) continue; // a repeated part counts once, at its first position
            var j = Array.IndexOf(pb, pa[i]);
            if (j >= 0) score += 1.0 / (Math.Max(i, j) + 1);
        }
        return score;
    }

    /// <summary>
    /// Items reordered by similarity. The first item starts a cluster, which then takes the remaining item most
    /// similar to any member (earliest wins a tie) until nothing is similar any more; the next cluster starts with the
    /// first item left. Items without similarity keep their relative order, as do rows without a name.
    /// </summary>
    public static List<T> Group<T>(IEnumerable<T> items, Func<T, string> name)
    {
        var remaining = items.Select(item => (Item: item, Name: name(item))).ToList();
        var result = new List<T>(remaining.Count);
        while (remaining.Count > 0)
        {
            var seed = remaining[0];
            remaining.RemoveAt(0);
            result.Add(seed.Item);
            if (string.IsNullOrWhiteSpace(seed.Name)) continue;

            // best[i] = similarity of remaining[i] to the closest member of the cluster so far
            var best = remaining.Select(r => Similarity(seed.Name, r.Name)).ToList();
            while (true)
            {
                var pick = -1;
                for (var i = 0; i < best.Count; i++)
                {
                    if (best[i] > 0 && (pick < 0 || best[i] > best[pick])) pick = i;
                }
                if (pick < 0) break;

                var member = remaining[pick];
                result.Add(member.Item);
                remaining.RemoveAt(pick);
                best.RemoveAt(pick);
                for (var i = 0; i < best.Count; i++)
                    best[i] = Math.Max(best[i], Similarity(member.Name, remaining[i].Name));
            }
        }
        return result;
    }

    [GeneratedRegex("[_-]+")]
    private static partial Regex SeparatorRun();
}
