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
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Amazon.Runtime.Endpoints
{
    /// <summary>
    /// 
    /// Property bag implementation.
    /// 
    /// Can store arbitrary data accessed by parameter/property name.
    /// Use [] indexer to store/retrieve data.
    /// e.g. bag["parameter"] = value;
    /// If property is not defined returns null.
    /// </summary>
    public class PropertyBag : IPropertyBag
    {
        private readonly Dictionary<string, object> properties;

        public PropertyBag()
        {
            properties = new Dictionary<string, object>();
        }

        /// <summary>
        /// Creates a bag holding a copy of the properties in <paramref name="source"/>. The dictionary is
        /// sized exactly, so this is cheaper than rebuilding the same bag entry by entry.
        /// </summary>
        internal PropertyBag(PropertyBag source)
        {
            properties = new Dictionary<string, object>(source.properties);
        }

        public object this[string propertyName]
        {
            get 
            {
                object result;
                if (properties.TryGetValue(propertyName, out result))
                {
                    return result;
                }
                return null;
            }
            set 
            { 
                properties[propertyName] = value; 
            }
        }

        private PropertyBag(int capacity)
        {
            properties = new Dictionary<string, object>(capacity);
        }

        /// <summary>
        /// Creates a deep copy of a bag built by <see cref="FromJsonElement(JsonElement)"/>. Nested bags and
        /// lists are copied; strings and boxed numbers and booleans are immutable and are shared.
        /// </summary>
        internal PropertyBag DeepCopy()
        {
            var copy = new PropertyBag(properties.Count);
            foreach (var kvp in properties)
            {
                copy.properties.Add(kvp.Key, DeepCopyValue(kvp.Value));
            }
            return copy;
        }

        private static object DeepCopyValue(object value)
        {
            var bag = value as PropertyBag;
            if (bag != null)
            {
                return bag.DeepCopy();
            }

            var list = value as List<object>;
            if (list != null)
            {
                var listCopy = new List<object>(list.Count);
                foreach (var item in list)
                {
                    listCopy.Add(DeepCopyValue(item));
                }
                return listCopy;
            }

            return value;
        }

        /// <summary>
        /// Returns true when <paramref name="other"/> holds exactly the same properties and values as this bag.
        /// Values are compared with <see cref="object.Equals(object, object)"/>.
        /// </summary>
        internal bool ContentEquals(PropertyBag other)
        {
            if (other == null || other.properties.Count != properties.Count)
            {
                return false;
            }

            foreach (var kvp in properties)
            {
                object otherValue;
                if (!other.properties.TryGetValue(kvp.Key, out otherValue) || !Equals(kvp.Value, otherValue))
                {
                    return false;
                }
            }

            return true;
        }

        // Matches the transcoding JsonDocument.Parse(string) does: invalid UTF-16 (a lone surrogate) is an error,
        // not silently replaced, so such input goes to the JsonDocument fallback and fails the same way it always has.
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// Transcodes <paramref name="json"/> to UTF-8 in a buffer rented from <see cref="ArrayPool{T}.Shared"/>.
        /// Returns null if the string is not valid UTF-16. The caller must return the buffer to the pool.
        /// The buffer never leaves the parser that rented it.
        /// </summary>
        internal static byte[] TryRentUtf8(string json, out int length)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(StrictUtf8.GetMaxByteCount(json.Length));
            try
            {
                length = StrictUtf8.GetBytes(json, 0, json.Length, buffer, 0);
                return buffer;
            }
            catch (EncoderFallbackException)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                length = 0;
                return null;
            }
        }

        /// <summary>
        /// Builds the same object tree as <see cref="FromJsonElement(JsonElement)"/> over JsonDocument.Parse,
        /// in one forward-only <see cref="Utf8JsonReader"/> pass and without building a document.
        /// Returns null whenever the input is not a valid JSON object made only of strings, numbers, booleans,
        /// objects and arrays (for example a null value, a number that does not fit in a double, or trailing
        /// content). The caller then falls back to the JsonDocument path, which produces the original exception.
        /// </summary>
        internal static PropertyBag TryParseJsonObject(string json)
        {
            byte[] buffer = TryRentUtf8(json, out int length);
            if (buffer == null)
            {
                return null;
            }

            try
            {
                var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer, 0, length));
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    return null;
                }

                var result = TryReadObject(ref reader);

                // The original parser rejects anything after the root value.
                if (result == null || reader.Read())
                {
                    return null;
                }
                return result;
            }
            catch (JsonException)
            {
                // Malformed JSON: the fallback throws the original exception.
                return null;
            }
            catch (InvalidOperationException)
            {
                // A string that cannot be decoded (for example an escaped lone surrogate): the fallback
                // decides between this and an earlier syntax error exactly as before.
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Reader is positioned on StartObject; on success it is left on the matching EndObject.
        private static PropertyBag TryReadObject(ref Utf8JsonReader reader)
        {
            var result = new PropertyBag();
            while (true)
            {
                if (!reader.Read())
                {
                    return null;
                }
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return result;
                }

                // TokenType is PropertyName here: the reader validates the document as it goes.
                string name = reader.GetString();
                if (!reader.Read() || !TryReadValue(ref reader, out object value))
                {
                    return null;
                }
                // Same as FromJsonElement: a repeated name keeps the last value.
                result[name] = value;
            }
        }

        // Reader is positioned on StartArray; on success it is left on the matching EndArray.
        private static List<object> TryReadArray(ref Utf8JsonReader reader)
        {
            var list = new List<object>();
            while (true)
            {
                if (!reader.Read())
                {
                    return null;
                }
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return list;
                }
                if (!TryReadValue(ref reader, out object value))
                {
                    return null;
                }
                list.Add(value);
            }
        }

        // Mirrors ElementToValue, including the order of the numeric conversions.
        private static bool TryReadValue(ref Utf8JsonReader reader, out object value)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    value = reader.GetString();
                    return true;
                case JsonTokenType.Number:
                    // ElementToValue's conditional expression has type double, so every number is boxed
                    // as a double after the first conversion that succeeds. Keep exactly that.
                    double number;
                    if (reader.TryGetInt32(out var intValue)) number = intValue;
                    else if (reader.TryGetInt64(out var longValue)) number = longValue;
                    else if (reader.TryGetUInt32(out var uintValue)) number = uintValue;
                    else if (reader.TryGetUInt64(out var ulongValue)) number = ulongValue;
                    else if (!reader.TryGetDouble(out number))
                    {
                        value = null;
                        return false;
                    }
                    value = number;
                    return true;
                case JsonTokenType.True:
                    value = true;
                    return true;
                case JsonTokenType.False:
                    value = false;
                    return true;
                case JsonTokenType.StartObject:
                    value = TryReadObject(ref reader);
                    return value != null;
                case JsonTokenType.StartArray:
                    value = TryReadArray(ref reader);
                    return value != null;
                default:
                    value = null;
                    return false;
            }
        }

        /// <summary>
        /// Creates PropertyBag based object tree from JsonElement.
        /// </summary>
        internal static PropertyBag FromJsonElement(JsonElement jsonData)
        {
            var result = new PropertyBag();
            foreach (JsonProperty property in jsonData.EnumerateObject())
            {
                result[property.Name] = ElementToValue(property.Value);
            }

            return result;
        }

        /// <summary>
        /// Return a value from a JsonElement node.
        /// Node value can be simple/final i.e. String, Int etc.
        /// or it can be complex i.e. Object, Array
        /// Translates Object json element to PropertyBag.
        /// Translates Array json element to List.
        /// </summary>
        private static object ElementToValue(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt32(out var intValue) ? intValue :
                                        element.TryGetInt64(out var longValue) ? longValue :
                                        element.TryGetUInt32(out var uintValue) ? uintValue :
                                        element.TryGetUInt64(out var ulongValue) ? ulongValue :
                                        element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Object => FromJsonElement(element),
                JsonValueKind.Array => ParseJsonArray(element),
                _ => throw new ArgumentException("Unsupported JSON value type."),
            };
        }
        /// <summary>
        /// Converts a JsonElement array into a list of objects.
        /// </summary>
        private static List<object> ParseJsonArray(JsonElement element)
        {
            var list = new List<object>();
            foreach (var item in element.EnumerateArray())
            {
                list.Add(ElementToValue(item));
            }
            return list;
        }
    }
}
