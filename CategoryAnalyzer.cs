using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public class CategoryAnalyzer
{
    private readonly Dictionary<string, int> _categoryFrequencies = new();
    private readonly Dictionary<HashSet<string>, int> _subsetFrequencies = new(HashSet<string>.CreateSetComparer());
    private readonly List<HashSet<string>> _noteCategorySets = new();
    private const int MinNotesForDeck = 3;

    public void Analyze(List<string> categories)
    {
        if (categories == null) return;
        var categorySet = new HashSet<string>(categories.Select(CleanCategory).Where(c => !string.IsNullOrEmpty(c)));
        if (!categorySet.Any()) return;

        _noteCategorySets.Add(categorySet);
    }

    public void FinalizeAnalysis()
    {
        // 1. Calculate single category frequencies
        foreach (var categorySet in _noteCategorySets)
        {
            foreach (var category in categorySet)
            {
                _categoryFrequencies.TryGetValue(category, out var count);
                _categoryFrequencies[category] = count + 1;
            }
        }

        // 2. Calculate frequencies of all subsets
        foreach (var noteSet in _noteCategorySets)
        {
            var subsets = GetPowerSet(noteSet);
            foreach (var subset in subsets)
            {
                if (subset.Any())
                {
                    _subsetFrequencies.TryGetValue(subset, out var count);
                    _subsetFrequencies[subset] = count + 1;
                }
            }
        }
    }

    public (string DeckName, List<string> Tags) ResolveDeckName(List<string> categories)
    {
        if (categories == null) return ("Default", new List<string>());
        var cleanedCategories = categories.Select(CleanCategory).Where(c => !string.IsNullOrEmpty(c)).ToHashSet();
        if (!cleanedCategories.Any())
        {
            return ("Default", new List<string>());
        }

        var candidateSubsets = GetPowerSet(cleanedCategories).Where(s => s.Any()).ToList();

        var validPaths = new List<HashSet<string>>();
        foreach (var path in candidateSubsets)
        {
            _subsetFrequencies.TryGetValue(path, out var supportCount);
            if (supportCount < MinNotesForDeck)
            {
                continue;
            }

            // Check if path is hierarchical based on individual frequencies
            var orderedPath = path.OrderByDescending(c => _categoryFrequencies.GetValueOrDefault(c, 0)).ToList();
            bool isHierarchical = true;
            for (int i = 0; i < orderedPath.Count - 1; i++)
            {
                if (_categoryFrequencies.GetValueOrDefault(orderedPath[i], 0) == _categoryFrequencies.GetValueOrDefault(orderedPath[i+1], 0))
                {
                    isHierarchical = false;
                    break;
                }
            }

            if (isHierarchical)
            {
                validPaths.Add(path);
            }
        }

        if (!validPaths.Any())
        {
            return ("Default", cleanedCategories.ToList());
        }

        // Select the best path
        var bestPath = validPaths
            .OrderByDescending(p => _subsetFrequencies[p]) // Rule 1: Highest Support Count
            .ThenByDescending(p => p.Count) // Rule 2: Longest Path
            .ThenBy(p => string.Join("::", p.OrderBy(c => c))) // Rule 3: Lexicographical Order
            .First();

        var deckPath = bestPath
            .OrderByDescending(c => _categoryFrequencies.GetValueOrDefault(c, 0))
            .ToList();

        var deckName = string.Join("::", deckPath);
        var tags = cleanedCategories.Except(bestPath).ToList();

        return (deckName, tags);
    }

    private IEnumerable<HashSet<string>> GetPowerSet(HashSet<string> set)
    {
        var list = set.ToList();
        int setSize = list.Count;
        int powerSetSize = 1 << setSize;

        for (int counter = 0; counter < powerSetSize; counter++)
        {
            var subset = new HashSet<string>();
            for (int i = 0; i < setSize; i++)
            {
                if ((counter & (1 << i)) > 0)
                {
                    subset.Add(list[i]);
                }
            }
            yield return subset;
        }
    }

    private string CleanCategory(string category)
    {
        if (string.IsNullOrEmpty(category)) return string.Empty;
        var match = Regex.Match(category, @"\[\[(?:.*[|/])?(.*?)\]\]");
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }
        return category.Trim();
    }
}
