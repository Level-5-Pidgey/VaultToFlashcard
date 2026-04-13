using System.Collections.Immutable;
using System.Reflection;
using NUnit.Framework;
using VaultToFlashcard;

namespace VaultToFlashcardTests;

public class CategoryAnalyzerTests
{
	private static readonly MethodInfo GetPowerSetMethod;
	private static readonly MethodInfo CleanCategoryMethod;

	static CategoryAnalyzerTests()
	{
		var type = typeof(CategoryAnalyzer);
		GetPowerSetMethod = type.GetMethod("GetPowerSet", BindingFlags.NonPublic | BindingFlags.Static)!;
		CleanCategoryMethod = type.GetMethod("CleanCategory", BindingFlags.NonPublic | BindingFlags.Static)!;
	}

	#region GetPowerSet Tests

	[Test]
	public void GetPowerSet_ZeroElements_ReturnsSingleEmptySet()
	{
		var result = ((IEnumerable<HashSet<string>>)GetPowerSetMethod.Invoke(null, [new HashSet<string>()])!).ToList();

		Assert.Multiple(() =>
		{
			Assert.That(result, Has.Count.EqualTo(1));
			Assert.That(result[0], Is.Empty);
		});
	}

	[Test]
	public void GetPowerSet_OneElement_ReturnsTwoSubsets()
	{
		var result = ((IEnumerable<HashSet<string>>)GetPowerSetMethod.Invoke(null, [new HashSet<string> { "A" }])!)
			.ToList();

		Assert.Multiple(() =>
		{
			Assert.That(result, Has.Count.EqualTo(2));
			Assert.That(result.Count(s => s.Count == 0), Is.EqualTo(1));
			Assert.That(result.Count(s => s.Count == 1 && s.Contains("A")), Is.EqualTo(1));
		});
	}

	[Test]
	public void GetPowerSet_TwoElements_ReturnsFourSubsets()
	{
		var result = ((IEnumerable<HashSet<string>>)GetPowerSetMethod.Invoke(null, [new HashSet<string> { "A", "B" }])!)
			.ToList();

		Assert.Multiple(() =>
		{
			Assert.That(result, Has.Count.EqualTo(4));
			Assert.That(result.Count(s => s.Count == 0), Is.EqualTo(1));
			Assert.That(result.Count(s => s.Count == 1), Is.EqualTo(2));
			Assert.That(result.Count(s => s.Count == 2), Is.EqualTo(1));
		});
	}

	[Test]
	public void GetPowerSet_ThreeElements_ReturnsEightSubsets()
	{
		var result =
			((IEnumerable<HashSet<string>>)GetPowerSetMethod.Invoke(null, [new HashSet<string> { "A", "B", "C" }])!)
			.ToList();

		Assert.Multiple(() =>
		{
			Assert.That(result, Has.Count.EqualTo(8));
			Assert.That(result.Count(s => s.Count == 0), Is.EqualTo(1));
			Assert.That(result.Count(s => s.Count == 1), Is.EqualTo(3));
			Assert.That(result.Count(s => s.Count == 2), Is.EqualTo(3));
			Assert.That(result.Count(s => s.Count == 3), Is.EqualTo(1));
		});
	}

	[Test]
	public void GetPowerSet_NoDuplicates()
	{
		var result =
			((IEnumerable<HashSet<string>>)GetPowerSetMethod.Invoke(null, [new HashSet<string> { "A", "B", "C" }])!)
			.ToList();

		var distinct = result.Distinct(HashSet<string>.CreateSetComparer()).ToList();
		Assert.That(result, Has.Count.EqualTo(distinct.Count));
	}

	#endregion

	#region CleanCategory Tests

	[Test]
	public void CleanCategory_SimpleCategory_ReturnsAsIs()
	{
		var result = (string)CleanCategoryMethod.Invoke(null, ["simple"])!;

		Assert.That(result, Is.EqualTo("simple"));
	}

	[Test]
	public void CleanCategory_WikiLinkSimple_ExtractsContent()
	{
		var result = (string)CleanCategoryMethod.Invoke(null, ["[[link]]"])!;

		Assert.That(result, Is.EqualTo("link"));
	}

	[Test]
	public void CleanCategory_WikiLinkWithAlias_ExtractsLinkPart()
	{
		var result = (string)CleanCategoryMethod.Invoke(null, ["[[alias|link]]"])!;

		Assert.That(result, Is.EqualTo("link"));
	}

	[Test]
	public void CleanCategory_WikiLinkWithSlash_ExtractsCorrectPortion()
	{
		var result = (string)CleanCategoryMethod.Invoke(null, ["[[path/to]]"])!;

		// Regex extracts content after last | or /
		Assert.That(result, Is.EqualTo("to"));
	}

	[Test]
	public void CleanCategory_Null_ReturnsEmpty()
	{
		var result = (string)CleanCategoryMethod.Invoke(null, [null!])!;

		Assert.That(result, Is.EqualTo(""));
	}

	[Test]
	public void CleanCategory_EmptyString_ReturnsEmpty()
	{
		var result = (string)CleanCategoryMethod.Invoke(null, [""])!;

		Assert.That(result, Is.EqualTo(""));
	}

	#endregion

	#region FinalizeAnalysis Tests

	[Test]
	public void FinalizeAnalysis_EmptyNoteCategorySets_YieldsEmptyFrequencies()
	{
		var analyzer = new CategoryAnalyzer();
		analyzer.FinalizeAnalysis();

		var categoryFreq = GetPrivateField<Dictionary<string, int>>(analyzer, "CategoryFrequencies");
		var subsetFreq = GetPrivateField<Dictionary<HashSet<string>, int>>(analyzer, "SubsetFrequencies");

		Assert.Multiple(() =>
		{
			Assert.That(categoryFreq, Is.Empty);
			Assert.That(subsetFreq, Is.Empty);
		});
	}

	[Test]
	public void FinalizeAnalysis_SingleNote_SetsFrequencyToOne()
	{
		var analyzer = new CategoryAnalyzer();
		analyzer.Analyze(["Programming", "CSharp"]);
		analyzer.FinalizeAnalysis();

		var categoryFreq = GetPrivateField<Dictionary<string, int>>(analyzer, "CategoryFrequencies");

		Assert.Multiple(() =>
		{
			Assert.That(categoryFreq["Programming"], Is.EqualTo(1));
			Assert.That(categoryFreq["CSharp"], Is.EqualTo(1));
		});
	}

	[Test]
	public void FinalizeAnalysis_MultipleNotes_CumulativeFrequencies()
	{
		var analyzer = new CategoryAnalyzer();
		analyzer.Analyze(["Programming"]);
		analyzer.Analyze(["Programming"]);
		analyzer.Analyze(["Programming", "CSharp"]);
		analyzer.FinalizeAnalysis();

		var categoryFreq = GetPrivateField<Dictionary<string, int>>(analyzer, "CategoryFrequencies");

		Assert.Multiple(() =>
		{
			Assert.That(categoryFreq["Programming"], Is.EqualTo(3));
			Assert.That(categoryFreq["CSharp"], Is.EqualTo(1));
		});
	}

	[Test]
	public void FinalizeAnalysis_PowerSetSubsets_TrackedCorrectly()
	{
		var analyzer = new CategoryAnalyzer();
		analyzer.Analyze(["A", "B"]);
		analyzer.FinalizeAnalysis();

		var subsetFreq = GetPrivateField<Dictionary<HashSet<string>, int>>(analyzer, "SubsetFrequencies");

		// Subsets: {A}, {B}, {A,B} - all should have frequency 1
		Assert.That(subsetFreq.Count, Is.EqualTo(3));
		foreach (var count in subsetFreq.Values) Assert.That(count, Is.EqualTo(1));
	}

	#endregion

	#region ResolveDeckName Tests

	[Test]
	public void ResolveDeckName_NullCategories_ReturnsDefaultDeck()
	{
		var analyzer = new CategoryAnalyzer();
		analyzer.FinalizeAnalysis();

		var (deckName, tags) = analyzer.ResolveDeckName(null);

		Assert.Multiple(() =>
		{
			Assert.That(deckName, Is.EqualTo("Default"));
			Assert.That(tags, Is.Empty);
		});
	}

	[Test]
	public void ResolveDeckName_EmptyCategories_ReturnsDefaultDeck()
	{
		var analyzer = new CategoryAnalyzer();
		analyzer.FinalizeAnalysis();

		var (deckName, tags) = analyzer.ResolveDeckName([]);

		Assert.Multiple(() =>
		{
			Assert.That(deckName, Is.EqualTo("Default"));
			Assert.That(tags, Is.Empty);
		});
	}

	[Test]
	public void ResolveDeckName_NoSubsetMeetsThreshold_ReturnsDefaultWithAllTags()
	{
		var analyzer = new CategoryAnalyzer();
		// Only 3 notes, but MinNotesForDeck is 5
		analyzer.Analyze(["Programming"]);
		analyzer.Analyze(["Programming"]);
		analyzer.Analyze(["Programming"]);
		analyzer.FinalizeAnalysis();

		var (deckName, tags) = analyzer.ResolveDeckName(["Programming", "CSharp"]);

		Assert.Multiple(() =>
		{
			Assert.That(deckName, Is.EqualTo("Default"));
			Assert.That(tags, Does.Contain("Programming"));
			Assert.That(tags, Does.Contain("CSharp"));
		});
	}

	[Test]
	public void ResolveDeckName_SingleCategoryMeetsThreshold_ReturnsThatCategory()
	{
		var analyzer = new CategoryAnalyzer();
		for (var i = 0; i < 5; i++) analyzer.Analyze(["Programming"]);
		analyzer.FinalizeAnalysis();

		var (deckName, tags) = analyzer.ResolveDeckName(["Programming"]);

		Assert.That(deckName, Is.EqualTo("Programming"));
	}

	[Test]
	public void ResolveDeckName_HierarchyDifferentFrequencies_FormsHierarchicalPath()
	{
		var analyzer = new CategoryAnalyzer();
		// "Parent" appears 10 times, "Child" appears only with Parent (5 times)
		for (var i = 0; i < 10; i++) analyzer.Analyze(["Parent"]);
		for (var i = 0; i < 5; i++) analyzer.Analyze(["Parent", "Child"]);
		analyzer.FinalizeAnalysis();

		var (deckName, tags) = analyzer.ResolveDeckName(["Parent", "Child"]);

		// Hierarchical because Parent and Child have different frequencies
		Assert.That(deckName, Is.EqualTo("Parent::Child"));
	}

	[Test]
	public void ResolveDeckName_NonHierarchySameFrequencies_NotHierarchical()
	{
		var analyzer = new CategoryAnalyzer();
		// Both appear 5 times together - same frequency means not hierarchical
		for (var i = 0; i < 5; i++) analyzer.Analyze(["TopicA", "TopicB"]);
		analyzer.FinalizeAnalysis();

		var (deckName, tags) = analyzer.ResolveDeckName(["TopicA", "TopicB"]);

		// Same frequency means not hierarchical, deck is a single category
		Assert.That(deckName, Is.Not.EqualTo("TopicA::TopicB"));
	}

	[Test]
	public void ResolveDeckName_SingleCategoryPrefersSecondBest()
	{
		var analyzer = new CategoryAnalyzer();
		// "BestSingle" appears 5 times alone, {"BestSingle", "Second"} also appears 5 times together
		for (var i = 0; i < 5; i++) analyzer.Analyze(["BestSingle"]);
		for (var i = 0; i < 5; i++) analyzer.Analyze(["BestSingle", "Second"]);
		analyzer.FinalizeAnalysis();

		var (deckName, tags) = analyzer.ResolveDeckName(["BestSingle", "Second"]);

		// When best is single-category, prefer second-best multi-category
		Assert.That(deckName, Is.EqualTo("BestSingle::Second"));
	}

	[Test]
	public void ResolveDeckName_TagsExcludeDeckPath_AndPrefixWithDeckName()
	{
		var analyzer = new CategoryAnalyzer();
		for (var i = 0; i < 5; i++) analyzer.Analyze(["Programming", "ExtraTag"]);
		analyzer.FinalizeAnalysis();

		var (deckName, tags) = analyzer.ResolveDeckName(["Programming", "ExtraTag"]);

		Assert.Multiple(() =>
		{
			Assert.That(deckName, Is.EqualTo("Programming"));
			Assert.That(tags, Does.Not.Contain("Programming"));
			Assert.That(tags, Does.Contain("Programming::ExtraTag"));
		});
	}

	[Test]
	public void ResolveDeckName_WhitespaceCategories_HandledCleanly()
	{
		var analyzer = new CategoryAnalyzer();
		analyzer.Analyze(["  ", ""]);
		analyzer.FinalizeAnalysis();

		var (deckName, tags) = analyzer.ResolveDeckName(["  ", ""]);

		Assert.That(deckName, Is.EqualTo("Default"));
	}

	[Test]
	public void ResolveDeckName_FinalizeWithoutAnalyze_HandlesGracefully()
	{
		var analyzer = new CategoryAnalyzer();
		analyzer.FinalizeAnalysis();

		var (deckName, tags) = analyzer.ResolveDeckName(["AnyCategory"]);

		Assert.That(deckName, Is.EqualTo("Default"));
	}

	#endregion

	#region Helper Methods

	private static T GetPrivateField<T>(object obj, string fieldName)
	{
		var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
		return (T)field!.GetValue(obj)!;
	}

	#endregion
}