using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public class CategoryAnalyzer
{
    private readonly Dictionary<string, int> _categoryFrequencies = new();
    private readonly List<HashSet<string>> _noteCategorySets = new();
    private Dictionary<string, string> _parentCache = new(); // Cache for parent-child relationships

    public void Analyze(List<string> categories)
    {
        if (categories == null) return;
        var categorySet = new HashSet<string>(categories.Select(CleanCategory));
        if (!categorySet.Any()) return;

        _noteCategorySets.Add(categorySet);
        foreach (var category in categorySet)
        {
            _categoryFrequencies.TryGetValue(category, out var count);
            _categoryFrequencies[category] = count + 1;
        }
    }

    public void FinalizeAnalysis()
    {
        // This is where we can pre-calculate hierarchies if needed.
        // For now, we'll do it on the fly in ResolveDeckName.
    }

    public string ResolveDeckName(List<string> categories)
    {
        if (categories == null) return "Default";
        var cleanedCategories = categories.Select(CleanCategory).ToHashSet();
        if (!cleanedCategories.Any())
        {
            return "Default"; // Or some other fallback
        }

        // Find the best hierarchical path for the given categories
        var path = FindLongestPath(cleanedCategories);

        return string.Join("::", path);
    }

    private IEnumerable<string> FindLongestPath(HashSet<string> categories)
    {
        if (!categories.Any())
        {
            return Enumerable.Empty<string>();
        }
        
        // Order the categories by their global frequency, descending.
        // This makes more general categories appear first.
        var orderedCategories = categories
            .OrderByDescending(c => _categoryFrequencies.GetValueOrDefault(c, 0))
            .ToList();

        // Simple approach: just join the ordered categories.
        // A more complex approach could validate subset relationships here if needed.
        return orderedCategories;
    }
    
    private string CleanCategory(string category)
    {
        if (string.IsNullOrEmpty(category)) return string.Empty;
        // Cleans categories like "[[DOM.base|DOM]]" to "DOM"
        var match = Regex.Match(category, @"\[\[(?:.*[|/])?(.*?)\]\]");
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }
        return category.Trim();
    }
}
