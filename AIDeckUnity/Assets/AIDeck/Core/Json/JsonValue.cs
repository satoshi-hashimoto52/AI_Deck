using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace AIDeck.Core.Json
{
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// Minimal immutable-ish JSON DOM. Exists so that AIDeck.Core can persist the
    /// library, settings and diagnostics without depending on UnityEngine or on any
    /// third-party (potentially paid / unclear-licence) serializer.
    /// Accessors never throw on a type mismatch: they return the supplied fallback.
    /// That is what makes FR-084 (repair a corrupt settings file instead of crashing)
    /// straightforward to implement.
    /// </summary>
    public sealed class JsonValue : IEnumerable<JsonValue>
    {
        public static readonly JsonValue Null = new JsonValue(JsonKind.Null, null);

        private readonly object _value;

        private JsonValue(JsonKind kind, object value)
        {
            Kind = kind;
            _value = value;
        }

        public JsonKind Kind { get; }

        public bool IsNull => Kind == JsonKind.Null;
        public bool IsObject => Kind == JsonKind.Object;
        public bool IsArray => Kind == JsonKind.Array;

        public static JsonValue Bool(bool v) => new JsonValue(JsonKind.Bool, v);
        public static JsonValue Number(double v) => new JsonValue(JsonKind.Number, v);
        public static JsonValue String(string v) =>
            v == null ? Null : new JsonValue(JsonKind.String, v);

        public static JsonValue NewArray() =>
            new JsonValue(JsonKind.Array, new List<JsonValue>());

        public static JsonValue NewObject() =>
            new JsonValue(JsonKind.Object, new Dictionary<string, JsonValue>(StringComparer.Ordinal));

        public static JsonValue Array(IEnumerable<JsonValue> items)
        {
            var list = new List<JsonValue>();
            if (items != null)
            {
                foreach (var item in items)
                {
                    list.Add(item ?? Null);
                }
            }

            return new JsonValue(JsonKind.Array, list);
        }

        private List<JsonValue> AsList => _value as List<JsonValue>;
        private Dictionary<string, JsonValue> AsMap => _value as Dictionary<string, JsonValue>;

        public int Count
        {
            get
            {
                if (AsList != null)
                {
                    return AsList.Count;
                }

                if (AsMap != null)
                {
                    return AsMap.Count;
                }

                return 0;
            }
        }

        public IEnumerable<string> Keys => AsMap != null ? (IEnumerable<string>)AsMap.Keys : System.Array.Empty<string>();

        /// <summary>Array element access. Out-of-range reads yield <see cref="Null"/>.</summary>
        public JsonValue this[int index]
        {
            get
            {
                var list = AsList;
                if (list == null || index < 0 || index >= list.Count)
                {
                    return Null;
                }

                return list[index];
            }
        }

        /// <summary>Object member access. Missing members yield <see cref="Null"/>.</summary>
        public JsonValue this[string key]
        {
            get
            {
                var map = AsMap;
                if (map == null || key == null || !map.TryGetValue(key, out var v))
                {
                    return Null;
                }

                return v;
            }
        }

        public bool Has(string key) => AsMap != null && key != null && AsMap.ContainsKey(key);

        public JsonValue Add(JsonValue item)
        {
            AsList?.Add(item ?? Null);
            return this;
        }

        public JsonValue Set(string key, JsonValue value)
        {
            if (AsMap != null && key != null)
            {
                AsMap[key] = value ?? Null;
            }

            return this;
        }

        public JsonValue Set(string key, string value) => Set(key, String(value));
        public JsonValue Set(string key, double value) => Set(key, Number(value));
        public JsonValue Set(string key, bool value) => Set(key, Bool(value));

        public bool AsBool(bool fallback = false) =>
            Kind == JsonKind.Bool ? (bool)_value : fallback;

        public double AsDouble(double fallback = 0d)
        {
            if (Kind != JsonKind.Number)
            {
                return fallback;
            }

            var d = (double)_value;
            return double.IsNaN(d) || double.IsInfinity(d) ? fallback : d;
        }

        public float AsFloat(float fallback = 0f) => (float)AsDouble(fallback);

        public int AsInt(int fallback = 0)
        {
            var d = AsDouble(double.NaN);
            if (double.IsNaN(d) || d > int.MaxValue || d < int.MinValue)
            {
                return fallback;
            }

            return (int)Math.Round(d, MidpointRounding.AwayFromZero);
        }

        public long AsLong(long fallback = 0L)
        {
            var d = AsDouble(double.NaN);
            if (double.IsNaN(d) || d > long.MaxValue || d < long.MinValue)
            {
                return fallback;
            }

            return (long)Math.Round(d, MidpointRounding.AwayFromZero);
        }

        public string AsString(string fallback = null) =>
            Kind == JsonKind.String ? (string)_value : fallback;

        public IEnumerator<JsonValue> GetEnumerator()
        {
            var list = AsList;
            if (list == null)
            {
                yield break;
            }

            foreach (var item in list)
            {
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => JsonWriter.Write(this, false);

        internal string RawString => _value as string;
        internal double RawNumber => _value is double d ? d : 0d;
        internal bool RawBool => _value is bool b && b;

        internal static string FormatNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                // JSON has no NaN/Infinity literal. Emitting 0 keeps the document
                // parseable; the safety clamps in the audio layer treat 0 as benign.
                return "0";
            }

            if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            {
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            }

            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
