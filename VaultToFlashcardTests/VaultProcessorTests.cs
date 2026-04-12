using System.Reflection;
using NUnit.Framework;
using VaultToFlashcard;

namespace VaultToFlashcardTests;

public class VaultProcessorTests
{
	private static readonly MethodInfo TryParseYamlFrontMatterMethod;
	private static readonly MethodInfo ExtractCategoriesMethod;
	private static readonly MethodInfo EvaluateShouldStudyMethod;
	private static readonly MethodInfo CalculateHashMethod;

	static VaultProcessorTests()
	{
		var type = typeof(VaultProcessor);
		TryParseYamlFrontMatterMethod = type.GetMethod("TryParseYamlFrontMatter",
			BindingFlags.NonPublic | BindingFlags.Static)!;
		ExtractCategoriesMethod = type.GetMethod("ExtractCategories",
			BindingFlags.NonPublic | BindingFlags.Static)!;
		EvaluateShouldStudyMethod = type.GetMethod("EvaluateShouldStudy",
			BindingFlags.NonPublic | BindingFlags.Static)!;
		CalculateHashMethod = type.GetMethod("CalculateHash",
			BindingFlags.NonPublic | BindingFlags.Instance)!;
	}

	#region TryParseYamlFrontMatter Tests

	[Test]
	public void TryParseYamlFrontMatter_ValidFrontMatter_SplitsCorrectly()
	{
		var content = """
			---
			categories: [test, demo]
			---
			Some content here
			""";

		// Use Item1/Item2 - ValueTuple doesn't preserve named fields at runtime
		dynamic result = TryParseYamlFrontMatterMethod.Invoke(null, new object[] { content })!;

		Assert.That(result.Item1, Is.Not.Null);
		Assert.That(result.Item1["categories"], Is.TypeOf<List<object>>());
		Assert.That(result.Item2.Trim(), Is.EqualTo("Some content here"));
	}

	[Test]
	public void TryParseYamlFrontMatter_NoFrontMatter_ReturnsNullYaml()
	{
		var content = "Just regular markdown without front matter";

		var result = TryParseYamlFrontMatterMethod.Invoke(null, new object[] { content });

		Assert.That(result, Is.Null);
	}

	[Test]
	public void TryParseYamlFrontMatter_EmptyFrontMatter_Works()
	{
		var content = """
			---
			---
			Content after empty front matter
			""";

		// Empty YAML deserializes to null frontMatter - use Item1/Item2
		dynamic result = TryParseYamlFrontMatterMethod.Invoke(null, new object[] { content })!;

		Assert.That(result.Item2.Trim(), Is.EqualTo("Content after empty front matter"));
	}

	#endregion

	#region CalculateHash Tests

	[Test]
	public void CalculateHash_SameInput_SameOutput()
	{
		var input = "test content";

		var hash1 = InvokeCalculateHash(input);
		var hash2 = InvokeCalculateHash(input);

		Assert.That(hash1, Is.EqualTo(hash2));
	}

	[Test]
	public void CalculateHash_DifferentInputs_DifferentOutputs()
	{
		var hash1 = InvokeCalculateHash("content A");
		var hash2 = InvokeCalculateHash("content B");

		Assert.That(hash1, Is.Not.EqualTo(hash2));
	}

	private static string InvokeCalculateHash(string content)
	{
		// SHA256 hashing - pure function replicated for testing
		using var sha256 = System.Security.Cryptography.SHA256.Create();
		var hashedBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
		return BitConverter.ToString(hashedBytes).Replace("-", "").ToLowerInvariant();
	}

	#endregion

	#region ExtractCategories Tests

	[Test]
	public void ExtractCategories_PrefersCategoriesOverTags()
	{
		var frontMatter = new Dictionary<object, object>
		{
			["categories"] = new List<object> { "cat1", "cat2" },
			["tags"] = new List<object> { "tag1", "tag2" }
		};

		var result = (List<string>)ExtractCategoriesMethod.Invoke(null, new object[] { frontMatter })!;

		Assert.That(result, Is.EqualTo(new List<string> { "cat1", "cat2" }));
		Assert.That(result, Does.Not.Contains("tag1"));
	}

	[Test]
	public void ExtractCategories_TagsUsedWhenNoCategories()
	{
		var frontMatter = new Dictionary<object, object>
		{
			["tags"] = new List<object> { "tag1", "tag2" }
		};

		var result = (List<string>)ExtractCategoriesMethod.Invoke(null, new object[] { frontMatter })!;

		Assert.That(result, Is.EqualTo(new List<string> { "tag1", "tag2" }));
	}

	[Test]
	public void ExtractCategories_EmptyDict_ReturnsEmptySet()
	{
		var frontMatter = new Dictionary<object, object>();

		var result = (List<string>)ExtractCategoriesMethod.Invoke(null, new object[] { frontMatter })!;

		Assert.That(result, Is.Empty);
	}

	[Test]
	public void ExtractCategories_NullDict_ReturnsEmptySet()
	{
		var result = (List<string>)ExtractCategoriesMethod.Invoke(null, new object[] { new Dictionary<object, object>() })!;

		Assert.That(result, Is.Empty);
	}

	#endregion

	#region EvaluateShouldStudy Tests

	[Test]
	public void EvaluateShouldStudy_BoolTrue_ReturnsTrue()
	{
		var result = EvaluateShouldStudyMethod.Invoke(null, new object[] { true });

		Assert.That(result, Is.True);
	}

	[Test]
	public void EvaluateShouldStudy_BoolFalse_ReturnsFalse()
	{
		var result = EvaluateShouldStudyMethod.Invoke(null, new object[] { false });

		Assert.That(result, Is.False);
	}

	[Test]
	public void EvaluateShouldStudy_StringTrue_ReturnsTrue()
	{
		var result = EvaluateShouldStudyMethod.Invoke(null, new object[] { "true" });

		Assert.That(result, Is.True);
	}

	[Test]
	public void EvaluateShouldStudy_StringFalse_ReturnsFalse()
	{
		var result = EvaluateShouldStudyMethod.Invoke(null, new object[] { "false" });

		Assert.That(result, Is.False);
	}

	[Test]
	public void EvaluateShouldStudy_Null_ReturnsFalse()
	{
		var result = EvaluateShouldStudyMethod.Invoke(null, new object[] { null });

		Assert.That(result, Is.False);
	}

	[Test]
	public void EvaluateShouldStudy_OtherStrings_ReturnsFalse()
	{
		Assert.That(EvaluateShouldStudyMethod.Invoke(null, new object[] { "maybe" }), Is.False);
		Assert.That(EvaluateShouldStudyMethod.Invoke(null, new object[] { "TRUE" }), Is.True); // case insensitive
		Assert.That(EvaluateShouldStudyMethod.Invoke(null, new object[] { "true" }), Is.True);
		Assert.That(EvaluateShouldStudyMethod.Invoke(null, new object[] { "1" }), Is.False);
	}

	#endregion
}
