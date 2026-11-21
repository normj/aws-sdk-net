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
using System.Collections.Generic;
using System.Text.Json;

namespace Amazon.Runtime.Endpoints
{
    /// <summary>
    /// Endpoint to be used when calling service operation.
    /// </summary>
    public class Endpoint
    {
        /// <summary>
        /// Constructor used by custorm EndpointProvider
        /// </summary>
        public Endpoint(string url) : this(url, null, null)
        {
        }

        /// <summary>
        /// Constructor used by code-generated EndpointProvider
        /// </summary>
        public Endpoint(string url, string attributesJson, string headersJson)
        {
            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentNullException("url");
            }
            
            URL = url;

            if (!string.IsNullOrEmpty(attributesJson))
            {
                Attributes = PropertyBag.TryParseJsonObject(attributesJson) ?? ParseAttributesWithJsonDocument(attributesJson);
            }

            if (!string.IsNullOrEmpty(headersJson))
            {
                Headers = TryParseHeaders(headersJson) ?? ParseHeadersWithJsonDocument(headersJson);
            }
        }

        /// <summary>
        /// Builds the attributes bag through <see cref="JsonDocument"/>. This is the original implementation;
        /// <see cref="PropertyBag.TryParseJsonObject(string)"/> defers to it for anything outside the common shape,
        /// so malformed or unusual input produces the same result or exception as before.
        /// </summary>
        private static PropertyBag ParseAttributesWithJsonDocument(string attributesJson)
        {
            using JsonDocument doc = JsonDocument.Parse(attributesJson);
            var attributes = doc.RootElement;
            return PropertyBag.FromJsonElement(attributes);
        }

        /// <summary>
        /// Parses a headers object of the form {"name": ["value", ...], ...} with a single forward-only
        /// <see cref="Utf8JsonReader"/> pass. Returns null whenever the input is anything other than a valid
        /// object whose values are arrays of strings or nulls (or other values, which produce an empty list),
        /// with no duplicate names. The caller then falls back to <see cref="ParseHeadersWithJsonDocument(string)"/>,
        /// which throws the same exceptions it always has.
        /// </summary>
        private static IDictionary<string, IList<string>> TryParseHeaders(string headersJson)
        {
            byte[] buffer = PropertyBag.TryRentUtf8(headersJson, out int length);
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

                var headers = new Dictionary<string, IList<string>>();
                while (true)
                {
                    if (!reader.Read())
                    {
                        return null;
                    }
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        break;
                    }

                    // TokenType is PropertyName here: the reader validates the document as it goes.
                    string name = reader.GetString();
                    if (headers.ContainsKey(name) || !reader.Read())
                    {
                        return null;
                    }

                    var headerValues = new List<string>();
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (true)
                        {
                            if (!reader.Read())
                            {
                                return null;
                            }
                            if (reader.TokenType == JsonTokenType.EndArray)
                            {
                                break;
                            }
                            if (reader.TokenType == JsonTokenType.String)
                            {
                                headerValues.Add(reader.GetString());
                            }
                            else if (reader.TokenType == JsonTokenType.Null)
                            {
                                headerValues.Add(null);
                            }
                            else
                            {
                                return null;
                            }
                        }
                    }
                    else if (!reader.TrySkip())
                    {
                        return null;
                    }

                    headers.Add(name, headerValues);
                }

                // The original parser rejects anything after the root value.
                if (reader.Read())
                {
                    return null;
                }
                return headers;
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
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Builds the headers dictionary through <see cref="JsonDocument"/>. This is the original implementation.
        /// </summary>
        private static IDictionary<string, IList<string>> ParseHeadersWithJsonDocument(string headersJson)
        {
            {
                using JsonDocument doc = JsonDocument.Parse(headersJson);
                var headers = doc.RootElement;
                var result = new Dictionary<string, IList<string>>();
                foreach (JsonProperty key in headers.EnumerateObject())
                {
                    var headerValues = new List<string>();
                    if (headers.TryGetProperty(key.Name, out JsonElement value) && value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in value.EnumerateArray())
                        {
                            headerValues.Add(item.GetString());
                        }
                    }
                    result.Add(key.Name, headerValues);
                }
                return result;
            }
        }

        /// <summary>
        /// Endpoint's url 
        /// </summary>
        public string URL { get; set; }

        /// <summary>
        /// Custom endpoint attributes
        /// </summary>
        public IPropertyBag Attributes { get; set; }

        /// <summary>
        /// Custom endpoint headers
        /// </summary>
        public IDictionary<string, IList<string>> Headers { get; set; }
    }
}
