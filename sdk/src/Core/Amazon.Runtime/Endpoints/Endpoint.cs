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
using System.Text.Json;
using System.Threading;

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
                Attributes = ParseAttributes(attributesJson);
            }

            if (!string.IsNullOrEmpty(headersJson))
            {
                using JsonDocument doc = JsonDocument.Parse(headersJson);
                var headers = doc.RootElement;
                Headers = new Dictionary<string, IList<string>>();
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
                    Headers.Add(key.Name, headerValues);
                }
            }
        }

        // Generated endpoint providers build the attributes JSON per request, but the text only
        // varies with a few inputs (region, auth scheme, signing name), so the same strings repeat.
        // Successfully parsed attribute trees are kept here by JSON text and every caller gets its
        // own deep copy, so a cached tree is never reachable outside this class. Invalid JSON is
        // never cached, so it keeps throwing on every call.
        private const int MaxCachedAttributes = 256;
        private const int MaxCachedAttributesLength = 4096;
        private static readonly ConcurrentDictionary<string, PropertyBag> _parsedAttributesCache =
            new ConcurrentDictionary<string, PropertyBag>(StringComparer.Ordinal);
        private static int _parsedAttributesCacheCount;

        private static PropertyBag ParseAttributes(string attributesJson)
        {
            PropertyBag cached;
            if (_parsedAttributesCache.TryGetValue(attributesJson, out cached))
            {
                return cached.DeepCopy();
            }

            PropertyBag parsed;
            using (JsonDocument doc = JsonDocument.Parse(attributesJson))
            {
                parsed = PropertyBag.FromJsonElement(doc.RootElement);
            }

            if (attributesJson.Length <= MaxCachedAttributesLength &&
                Volatile.Read(ref _parsedAttributesCacheCount) < MaxCachedAttributes &&
                _parsedAttributesCache.TryAdd(attributesJson, parsed.DeepCopy()))
            {
                Interlocked.Increment(ref _parsedAttributesCacheCount);
            }

            return parsed;
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
