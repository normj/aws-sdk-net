/*
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 * 
 *  http://aws.amazon.com/apache2.0
 * 
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using ThirdParty.RuntimeBackports;

namespace Amazon.Runtime.Internal.Endpoints.StandardLibrary
{
    /// <summary>
    /// A JSON template for <see cref="Fn.InterpolateJson(string, Dictionary{string, object})"/>, split once into the
    /// output text that never changes and the string values that contain placeholders. Applying it interpolates
    /// only those string values and JSON-escapes them, instead of parsing and re-serializing the whole document.
    /// A template is only used after it reproduced the original implementation's output exactly.
    /// </summary>
    internal sealed class InterpolatedJsonTemplate
    {
        // Generated endpoint providers pass string literals, so the same template instance repeats on every
        // request. Keyed by reference: lookups do not hash the text, and a template that is not a literal
        // simply misses. A null value marks a template that is not cached and takes the original path.
        private const int MaxCachedTemplates = 1024;
        private const int MaxCachedTemplateLength = 8192;
        private static readonly ConcurrentDictionary<string, InterpolatedJsonTemplate> _cache =
            new ConcurrentDictionary<string, InterpolatedJsonTemplate>(ReferenceComparer.Instance);
        private static int _cacheCount;

        // Output text between the dynamic values; always one more than _dynamicValues.
        private readonly string[] _literals;
        // The original string values that contain '{' or '}', in document order.
        private readonly string[] _dynamicValues;
        private readonly int _literalLength;

        private InterpolatedJsonTemplate(string[] literals, string[] dynamicValues)
        {
            _literals = literals;
            _dynamicValues = dynamicValues;
            foreach (var literal in literals)
            {
                _literalLength += literal.Length;
            }
        }

        /// <summary>
        /// Same result as <paramref name="uncached"/>(json, refs), using a cached template when one exists.
        /// </summary>
        internal static string Interpolate(string json, Dictionary<string, object> refs, Func<string, Dictionary<string, object>, string> uncached)
        {
            InterpolatedJsonTemplate template;
            if (_cache.TryGetValue(json, out template))
            {
                return template != null ? template.Apply(refs) : uncached(json, refs);
            }

            var result = uncached(json, refs);

            if (Volatile.Read(ref _cacheCount) < MaxCachedTemplates)
            {
                template = json.Length <= MaxCachedTemplateLength ? TryCreate(json, refs, result) : null;
                if (_cache.TryAdd(json, template))
                {
                    Interlocked.Increment(ref _cacheCount);
                }
            }

            return result;
        }

        private string Apply(Dictionary<string, object> refs)
        {
            if (_dynamicValues.Length == 0)
            {
                return _literals[0];
            }

            var result = new ValueStringBuilder(_literalLength + 32 * _dynamicValues.Length);
            try
            {
                for (int i = 0; i < _dynamicValues.Length; i++)
                {
                    result.Append(_literals[i]);
                    AppendJsonString(ref result, Fn.Interpolate(_dynamicValues[i], refs));
                }
                result.Append(_literals[_literals.Length - 1]);
                return result.ToString();
            }
            catch (JsonException)
            {
                // The original implementation turns any JsonException into an empty result.
                return string.Empty;
            }
            finally
            {
                result.Dispose();
            }
        }

        /// <summary>
        /// Appends <paramref name="value"/> exactly as Utf8JsonWriter.WriteStringValue with default options writes it.
        /// </summary>
        private static void AppendJsonString(ref ValueStringBuilder builder, string value)
        {
            if (IsPlainAscii(value))
            {
                builder.Append('"');
                builder.Append(value);
                builder.Append('"');
                return;
            }

            using (var stream = new MemoryStream(value.Length + 16))
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStringValue(value);
                    writer.Flush();
                }
                builder.Append(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
            }
        }

        // Characters the default JSON encoder never escapes. Anything else takes the writer path.
        private static bool IsPlainAscii(string value)
        {
            foreach (var c in value)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                      c == '-' || c == '.' || c == '_' || c == ':' || c == '/' || c == '*' || c == ','))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Builds a template for <paramref name="json"/>, or returns null when the template cannot be split safely
        /// or does not reproduce <paramref name="expected"/>, the original implementation's output for these refs.
        /// </summary>
        private static InterpolatedJsonTemplate TryCreate(string json, Dictionary<string, object> refs, string expected)
        {
            try
            {
                var markerPrefix = "awsjsontemplate" + Guid.NewGuid().ToString("N");
                if (json.IndexOf(markerPrefix, StringComparison.Ordinal) >= 0)
                {
                    return null;
                }

                var dynamicValues = new List<string>();
                string skeleton;
                using (var doc = JsonDocument.Parse(json))
                {
                    using (var stream = new MemoryStream(json.Length + 64))
                    {
                        using (var writer = new Utf8JsonWriter(stream))
                        {
                            WriteSkeleton(doc.RootElement, writer, dynamicValues, markerPrefix);
                            writer.Flush();
                        }
                        skeleton = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                    }
                }

                var literals = new string[dynamicValues.Count + 1];
                int start = 0;
                for (int i = 0; i < dynamicValues.Count; i++)
                {
                    var marker = "\"" + markerPrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";
                    var pos = skeleton.IndexOf(marker, start, StringComparison.Ordinal);
                    if (pos < 0)
                    {
                        return null;
                    }
                    literals[i] = skeleton.Substring(start, pos - start);
                    start = pos + marker.Length;
                }
                literals[literals.Length - 1] = skeleton.Substring(start);
                if (literals[literals.Length - 1].IndexOf(markerPrefix, StringComparison.Ordinal) >= 0)
                {
                    return null;
                }

                var template = new InterpolatedJsonTemplate(literals, dynamicValues.ToArray());
                return string.Equals(template.Apply(refs), expected, StringComparison.Ordinal) ? template : null;
            }
#pragma warning disable CA1031 // Any failure means the template is not cached; the original path still runs.
            catch (Exception)
            {
                return null;
            }
#pragma warning restore CA1031
        }

        // Mirrors Fn.InterpolateJson's writer walk, writing a marker string in place of each string value that
        // contains a placeholder. String values without '{' or '}' interpolate to themselves.
        private static void WriteSkeleton(JsonElement element, Utf8JsonWriter writer, List<string> dynamicValues, string markerPrefix)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        WriteSkeleton(property.Value, writer, dynamicValues, markerPrefix);
                    }
                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteSkeleton(item, writer, dynamicValues, markerPrefix);
                    }
                    writer.WriteEndArray();
                    break;

                case JsonValueKind.String:
                    var value = element.GetString();
                    if (value.IndexOfAny(Braces) >= 0)
                    {
                        writer.WriteStringValue(markerPrefix + dynamicValues.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        dynamicValues.Add(value);
                    }
                    else
                    {
                        writer.WriteStringValue(value);
                    }
                    break;

                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private static readonly char[] Braces = { '{', '}' };

        private sealed class ReferenceComparer : IEqualityComparer<string>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(string x, string y) => ReferenceEquals(x, y);
            public int GetHashCode(string obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
