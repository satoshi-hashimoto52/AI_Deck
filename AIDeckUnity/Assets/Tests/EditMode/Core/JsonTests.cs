using AIDeck.Core.Json;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>
    /// The JSON layer backs the library and settings files. FR-084 requires a corrupt
    /// settings file to be repaired rather than to crash the app, which means the parser
    /// must fail as data, not as an exception.
    /// </summary>
    [TestFixture]
    public class JsonTests
    {
        [Test]
        public void RoundTrip_PreservesValues()
        {
            var root = JsonValue.NewObject()
                .Set("name", "Deck A")
                .Set("gain", 0.75)
                .Set("muted", false);

            var array = JsonValue.NewArray();
            array.Add(JsonValue.Number(1));
            array.Add(JsonValue.Number(2.5));
            root.Set("values", array);

            var text = JsonWriter.Write(root);
            var parsed = JsonParser.Parse(text);

            Assert.That(parsed["name"].AsString(), Is.EqualTo("Deck A"));
            Assert.That(parsed["gain"].AsDouble(), Is.EqualTo(0.75));
            Assert.That(parsed["muted"].AsBool(true), Is.False);
            Assert.That(parsed["values"].Count, Is.EqualTo(2));
            Assert.That(parsed["values"][1].AsDouble(), Is.EqualTo(2.5));
        }

        [Test]
        public void MissingMembers_YieldNullNotException()
        {
            var parsed = JsonParser.Parse("{\"a\":1}");
            Assert.That(parsed["missing"].IsNull, Is.True);
            Assert.That(parsed["missing"].AsInt(42), Is.EqualTo(42));
            Assert.That(parsed["missing"]["deeper"].AsString("fallback"), Is.EqualTo("fallback"));
        }

        [Test]
        public void TypeMismatch_ReturnsFallback()
        {
            var parsed = JsonParser.Parse("{\"a\":\"text\"}");
            Assert.That(parsed["a"].AsDouble(9d), Is.EqualTo(9d));
            Assert.That(parsed["a"].AsBool(true), Is.True);
        }

        [Test]
        public void OutOfRangeArrayIndex_ReturnsNull()
        {
            var parsed = JsonParser.Parse("[1,2,3]");
            Assert.That(parsed[7].IsNull, Is.True);
            Assert.That(parsed[-1].IsNull, Is.True);
        }

        [TestCase("")]
        [TestCase("{")]
        [TestCase("{\"a\":}")]
        [TestCase("[1,2")]
        [TestCase("{\"a\":1}trailing")]
        [TestCase("nul")]
        [TestCase("\"unterminated")]
        public void MalformedDocuments_FailWithoutThrowing(string text)
        {
            Assert.That(JsonParser.TryParse(text, out _, out var error), Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void DeeplyNestedDocument_IsRejectedRatherThanOverflowingTheStack()
        {
            var text = new string('[', JsonParser.MaxDepth + 5) + new string(']', JsonParser.MaxDepth + 5);
            Assert.That(JsonParser.TryParse(text, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("nesting"));
        }

        [Test]
        public void Escapes_RoundTrip()
        {
            var value = "line\nbreak\t\"quoted\"\\slash\u00e9";
            var text = JsonWriter.Write(JsonValue.NewObject().Set("s", value));
            var parsed = JsonParser.Parse(text);
            Assert.That(parsed["s"].AsString(), Is.EqualTo(value));
        }

        [Test]
        public void NonFiniteNumbers_AreWrittenAsZeroSoTheDocumentStaysParseable()
        {
            var text = JsonWriter.Write(JsonValue.NewObject().Set("n", double.NaN).Set("i", double.PositiveInfinity));
            Assert.That(JsonParser.TryParse(text, out var parsed, out _), Is.True);
            Assert.That(parsed["n"].AsDouble(-1d), Is.EqualTo(0d));
            Assert.That(parsed["i"].AsDouble(-1d), Is.EqualTo(0d));
        }

        [Test]
        public void CompactAndIndentedFormsParseIdentically()
        {
            var root = JsonValue.NewObject().Set("a", 1).Set("b", "two");
            var compact = JsonParser.Parse(JsonWriter.Write(root, false));
            var indented = JsonParser.Parse(JsonWriter.Write(root, true));
            Assert.That(compact["a"].AsInt(), Is.EqualTo(indented["a"].AsInt()));
            Assert.That(compact["b"].AsString(), Is.EqualTo(indented["b"].AsString()));
        }
    }
}
