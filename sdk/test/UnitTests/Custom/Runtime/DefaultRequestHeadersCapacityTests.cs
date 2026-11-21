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
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Amazon.Runtime;
using Amazon.Runtime.Internal;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// DefaultRequest.Headers starts with an initial capacity. These tests pin what callers can observe:
    /// the dictionary starts empty, compares keys ordinally ignoring case, enumerates in insertion order
    /// (also past the initial capacity and after removals), and handles null keys as before.
    /// </summary>
    [TestClass]
    public class DefaultRequestHeadersCapacityTests
    {
        private sealed class TestRequest : AmazonWebServiceRequest { }

        private static IRequest NewRequest() => new DefaultRequest(new TestRequest(), "TestService");

        [TestMethod]
        public void NewRequest_HeadersAreEmptyAndIgnoreCase()
        {
            var headers = NewRequest().Headers;
            Assert.AreEqual(0, headers.Count);
            Assert.IsFalse(headers.IsReadOnly);

            headers["Content-Type"] = "application/json";
            Assert.AreEqual("application/json", headers["content-type"]);
            Assert.AreEqual("application/json", headers["CONTENT-TYPE"]);
            Assert.IsTrue(headers.ContainsKey("cOnTeNt-TyPe"));

            headers["CONTENT-TYPE"] = "text/plain";
            Assert.AreEqual(1, headers.Count);
            Assert.AreEqual("Content-Type", headers.Keys.Single(), "Replacing a value keeps the first key's casing");
            Assert.AreEqual("text/plain", headers["Content-Type"]);

            Assert.ThrowsExactly<ArgumentException>(() => headers.Add("content-type", "x"));
        }

        [TestMethod]
        public void Headers_UseOrdinalNotCultureComparison()
        {
            var headers = NewRequest().Headers;
            headers["x-amz-\u0130d"] = "dotted";      // LATIN CAPITAL LETTER I WITH DOT ABOVE
            Assert.IsFalse(headers.ContainsKey("x-amz-id"));
            Assert.IsFalse(headers.ContainsKey("x-amz-\u0131d")); // LATIN SMALL LETTER DOTLESS I
            headers["x-amz-ss"] = "ss";
            Assert.IsFalse(headers.ContainsKey("x-amz-\u00df"));  // LATIN SMALL LETTER SHARP S
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(9)]
        [DataRow(16)]
        [DataRow(17)]
        [DataRow(18)]
        [DataRow(40)]
        public void Headers_EnumerateInInsertionOrder(int count)
        {
            var headers = NewRequest().Headers;
            var expected = Enumerable.Range(0, count).Select(i => "Header-" + i).ToList();
            foreach (var name in expected)
                headers[name] = name.ToLowerInvariant();

            Assert.AreEqual(count, headers.Count);
            CollectionAssert.AreEqual(expected, headers.Keys.ToList());
            CollectionAssert.AreEqual(expected.Select(n => n.ToLowerInvariant()).ToList(), headers.Values.ToList());
            CollectionAssert.AreEqual(expected, headers.Select(kvp => kvp.Key).ToList());
        }

        [TestMethod]
        public void Headers_RemoveThenAddReusesFreedSlotsAsBefore()
        {
            // A dictionary reuses the most recently freed entry first, independent of its capacity, so the
            // enumeration order after removals is the same as with the default capacity.
            var headers = NewRequest().Headers;
            var reference = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in new IDictionary<string, string>[] { headers, reference })
            {
                for (int i = 0; i < 20; i++)
                    d["H" + i] = i.ToString();
                d.Remove("h3");
                d.Remove("H10");
                d.Remove("h0");
                d["New-A"] = "a";
                d["New-B"] = "b";
                d["H3"] = "3 again";
                d["New-C"] = "c";
            }

            CollectionAssert.AreEqual(reference.ToList(), headers.ToList());
        }

        [TestMethod]
        public void Headers_NullKeyThrowsAsBefore()
        {
            var headers = NewRequest().Headers;
            Assert.ThrowsExactly<ArgumentNullException>(() => headers[null] = "x");
            Assert.ThrowsExactly<ArgumentNullException>(() => headers.ContainsKey(null));
            headers["A"] = null;
            Assert.IsNull(headers["a"]);
        }

        [TestMethod]
        public void Headers_EachRequestHasItsOwnDictionary()
        {
            var first = NewRequest();
            var second = NewRequest();
            first.Headers["X-Test"] = "1";
            Assert.AreNotSame(first.Headers, second.Headers);
            Assert.AreEqual(0, second.Headers.Count);
        }
    }
}
