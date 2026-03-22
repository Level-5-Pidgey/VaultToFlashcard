using System.Text.RegularExpressions;

namespace VaultToFlashcard;

public partial class CategoryAnalyzer
{
    private readonly Dictionary<string, int> CategoryFrequencies = new();
    private readonly Dictionary<HashSet<string>, int> SubsetFrequencies = new(HashSet<string>.CreateSetComparer());
    private readonly List<HashSet<string>> NoteCategorySets = [];

    private const int MinNotesForDeck = 3;

    public void Analyze(List<string>? categories)
    {
        if (categories == null)
        {
            return;
        }
        
        var categorySet = new HashSet<string>(categories.Select(CleanCategory).Where(c => !string.IsNullOrEmpty(c)));
        if (!categorySet.Any()) return;

        NoteCategorySets.Add(categorySet);
    }

    public void FinalizeAnalysis()
    {
        foreach (var categorySet in NoteCategorySets)
        {
            foreach (var category in categorySet)
            {
                CategoryFrequencies.TryGetValue(category, out var count);
                CategoryFrequencies[category] = count + 1;
            }
        }

        foreach (var noteSet in NoteCategorySets)
        {
            var subsets = GetPowerSet(noteSet);
            foreach (var subset in subsets)
            {
                if (!subset.Any())
                {
                    continue;
                }

                SubsetFrequencies.TryGetValue(subset, out var count);
                SubsetFrequencies[subset] = count + 1;
            }
        }
    }

    public (string DeckName, IReadOnlyCollection<string> Tags) ResolveDeckName(List<string>? categories)
    {
        if (categories == null)
        {
            return ("Default", []);
        }
        
        var cleanedCategories = categories
            .Select(CleanCategory)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToHashSet();

        if (!cleanedCategories.Any())
        {
            return ("Default", []);
        }

        var candidateSubsets = GetPowerSet(cleanedCategories)
            .Where(s => s.Any());

        var validPaths = new List<HashSet<string>>();
        foreach (var path in candidateSubsets)
        {
            SubsetFrequencies.TryGetValue(path, out var supportCount);
            if (supportCount < MinNotesForDeck)
            {
                continue;
            }

            // Check if path is hierarchical based on individual frequencies
            var orderedPath = path.OrderByDescending(c => CategoryFrequencies.GetValueOrDefault(c, 0)).ToList();
            var isHierarchical = true;
            for (var i = 0; i < orderedPath.Count - 1; i++)
            {
                if (CategoryFrequencies.GetValueOrDefault(orderedPath[i], 0) !=
                    CategoryFrequencies.GetValueOrDefault(orderedPath[i + 1], 0))
                {
                    continue;
                }

                isHierarchical = false;
                break;
            }

            if (isHierarchical)
            {
                validPaths.Add(path);
            }
        }

        if (!validPaths.Any())
        {
            return ("Default", cleanedCategories);
        }

        // Select the best path
        
        /*
         * Resolve the best path by:
         * 1) The highest supporting count
         * 2) The longest path
         * 3) Lexographical order
         */
        var bestPath = validPaths
            .OrderByDescending(p => SubsetFrequencies[p])
            .ThenByDescending(p => p.Count)
            .ThenBy(p => string.Join("::", p.OrderBy(c => c)))
            .First();

        var deckPath = bestPath
            .OrderByDescending(c => CategoryFrequencies.GetValueOrDefault(c, 0))
            .ToList();

        var deckName = string.Join("::", deckPath);
        var tags = cleanedCategories
            .Except(bestPath)
            .ToArray();

        return (deckName, tags);
    }

    private static IEnumerable<HashSet<string>> GetPowerSet(HashSet<string> set)
    {
        var list = set.ToList();
        var setSize = list.Count;
        var powerSetSize = 1 << setSize;

        for (var counter = 0; counter < powerSetSize; counter++)
        {
            var subset = new HashSet<string>();
            for (var i = 0; i < setSize; i++)
            {
                if ((counter & (1 << i)) > 0)
                {
                    subset.Add(list[i]);
                }
            }

            yield return subset;
        }
    }

    private static string CleanCategory(string category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return string.Empty;
        }

        var match = WikiLinkRegex().Match(category);

        return match.Success ? 
            match.Groups[1].Value.Trim() : 
            category.Trim();
    }

    // TODO move this to a common location as it's used in the other file as well
    [GeneratedRegex(@"\[\[(?:.*[|/])?(.*?)\]\]")]
    private static partial Regex WikiLinkRegex();
}