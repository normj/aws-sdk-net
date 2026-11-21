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
using Amazon.Runtime.Endpoints;
using Amazon.Runtime.Internal.Endpoints.StandardLibrary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins how <see cref="Fn.GetAttr(object, string)"/> and <see cref="Fn.Interpolate"/> look up
    /// property bag members and refs, including missing names and custom bag implementations.
    /// </summary>
    [TestClass]
    public class EndpointsGetAttrTests
    {
        // Re-implements the interface, so its indexer is not PropertyBag's.
        private class ReimplementingBag : PropertyBag, IPropertyBag
        {
            object IPropertyBag.this[string propertyName]
            {
                get => "custom:" + propertyName;
                set { }
            }
        }

        private class RecordingBag : IPropertyBag
        {
            public readonly List<string> Requested = new List<string>();
            public object this[string propertyName]
            {
                get { Requested.Add(propertyName); return propertyName == "inner" ? (object)this : "v:" + propertyName; }
                set { }
            }
        }

        private class DerivedPartition : Partition
        {
        }

        private static PropertyBag Bag()
        {
            var inner = new PropertyBag();
            inner["leaf"] = "leafValue";
            inner["list"] = new List<object> { "i0", "i1" };
            var bag = new PropertyBag();
            bag["name"] = "aws";
            bag["inner"] = inner;
            bag["list"] = new List<object> { "a", "b", "c" };
            bag["number"] = 42;
            bag["nullValue"] = null;
            return bag;
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void GetAttrSingleSegment()
        {
            var bag = Bag();
            Assert.AreEqual("aws", Fn.GetAttr(bag, "name"));
            Assert.AreEqual(42, Fn.GetAttr(bag, "number"));
            Assert.IsNull(Fn.GetAttr(bag, "nullValue"));
            Assert.IsNull(Fn.GetAttr(bag, "missing"));
            Assert.IsNull(Fn.GetAttr(bag, "Name"), "lookups are case-sensitive");
            Assert.IsNull(Fn.GetAttr(bag, " name"));
            Assert.IsNull(Fn.GetAttr(bag, "name]"));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void GetAttrPathsAndIndexers()
        {
            var bag = Bag();
            Assert.AreEqual("leafValue", Fn.GetAttr(bag, "inner.leaf"));
            Assert.IsNull(Fn.GetAttr(bag, "inner.missing"));
            Assert.AreEqual("b", Fn.GetAttr(bag, "list[1]"));
            Assert.IsNull(Fn.GetAttr(bag, "list[3]"));
            Assert.IsNull(Fn.GetAttr(bag, "list[-1]"));
            Assert.AreEqual("i1", Fn.GetAttr(bag, "inner.list[1]"));
            Assert.AreEqual("b", Fn.GetAttr(new List<object> { "a", "b" }, "[1]"));
            Assert.ThrowsExactly<ArgumentException>(() => Fn.GetAttr(bag, "missing[0]"));
            Assert.ThrowsExactly<ArgumentException>(() => Fn.GetAttr(bag, "name.x"));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void GetAttrErrors()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => Fn.GetAttr(Bag(), null));
            Assert.ThrowsExactly<ArgumentNullException>(() => Fn.GetAttr(Bag(), ""));
            var e = Assert.ThrowsExactly<ArgumentException>(() => Fn.GetAttr("not a bag", "name"));
            Assert.AreEqual("Object addressing by pathing segment must be IPropertyBag", e.Message);
            e = Assert.ThrowsExactly<ArgumentException>(() => Fn.GetAttr(null, "name"));
            Assert.AreEqual("Object addressing by pathing segment must be IPropertyBag", e.Message);
            Assert.ThrowsExactly<ArgumentException>(() => Fn.GetAttr(null, "a.b"));
            Assert.ThrowsExactly<InvalidCastException>(() => Fn.GetAttr("not a bag", "a[0]"));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void GetAttrUsesCustomIndexers()
        {
            var reimplementing = new ReimplementingBag();
            Assert.AreEqual("custom:name", Fn.GetAttr(reimplementing, "name"));
            Assert.AreEqual("custom:name", Fn.GetAttr(new Dictionary<string, object> { ["r"] = reimplementing }["r"], "name"));

            var outer = new PropertyBag();
            outer["r"] = reimplementing;
            Assert.AreEqual("custom:x", Fn.GetAttr(outer, "r.x"));
            Assert.AreEqual("custom:x", Fn.Interpolate("{o#r.x}", new Dictionary<string, object> { ["o"] = outer }));

            var recording = new RecordingBag();
            Assert.AreEqual("v:leaf", Fn.GetAttr(recording, "inner.leaf"));
            CollectionAssert.AreEqual(new[] { "inner", "leaf" }, recording.Requested);
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void GetAttrOnCoreBagTypes()
        {
            var partition = Fn.Partition("us-west-2");
            Assert.AreEqual("aws", Fn.GetAttr(partition, "name"));
            Assert.AreEqual("amazonaws.com", Fn.GetAttr(new Dictionary<string, object> { ["p"] = partition }["p"], "dnsSuffix"));
            Assert.AreEqual("amazonaws.com", Fn.Interpolate("{p#dnsSuffix}", new Dictionary<string, object> { ["p"] = partition }));
            Assert.AreEqual(true, Fn.GetAttr(partition, "supportsFIPS"));

            var arn = Fn.ParseArn("arn:aws:s3:us-west-2:123456789012:accesspoint:my-endpoint");
            Assert.AreEqual("us-west-2", Fn.GetAttr(arn, "region"));
            Assert.AreEqual("my-endpoint", Fn.GetAttr(arn, "resourceId[1]"));
            Assert.AreEqual("s3", Fn.Interpolate("{a#service}", new Dictionary<string, object> { ["a"] = arn }));

            var url = Fn.ParseURL("https://example.com/path");
            Assert.AreEqual(false, Fn.GetAttr(url, "isIp"));
            Assert.AreEqual("https://example.com/path", Fn.Interpolate("{u#scheme}://{u#authority}{u#path}", new Dictionary<string, object> { ["u"] = url }));

            var derived = new DerivedPartition { name = "derived" };
            Assert.AreEqual("derived", Fn.Interpolate("{d#name}", new Dictionary<string, object> { ["d"] = derived }));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void InterpolateRefLookups()
        {
            var refs = new Dictionary<string, object>
            {
                ["Region"] = "us-west-2",
                ["Bucket"] = "bucket",
                ["bag"] = Bag(),
                ["n"] = 7,
            };
            Assert.AreEqual("https://bucket.s3.us-west-2.aws/7", Fn.Interpolate("https://{Bucket}.s3.{Region}.{bag#name}/{n}", refs));
            Assert.AreEqual("leafValue|b", Fn.Interpolate("{bag#inner.leaf}|{bag#list[1]}", refs));
            Assert.AreEqual("aws", Fn.Interpolate("{bag#name#ignored}", refs));
            Assert.AreEqual("{Region}", Fn.Interpolate("{{Region}}", refs));

            var e = Assert.ThrowsExactly<KeyNotFoundException>(() => Fn.Interpolate("{Missing}", refs));
            StringAssert.Contains(e.Message, "Missing");
            e = Assert.ThrowsExactly<KeyNotFoundException>(() => Fn.Interpolate("{region}", refs));
            StringAssert.Contains(e.Message, "region");
            e = Assert.ThrowsExactly<KeyNotFoundException>(() => Fn.Interpolate("{Missing#name}", refs));
            StringAssert.Contains(e.Message, "Missing");
            Assert.ThrowsExactly<KeyNotFoundException>(() => Fn.Interpolate("{}", refs));
            Assert.ThrowsExactly<NullReferenceException>(() => Fn.Interpolate("{bag#missing}", refs));
            Assert.ThrowsExactly<NullReferenceException>(() => Fn.Interpolate("{Region}", null));
            Assert.AreEqual("plain", Fn.Interpolate("plain", null));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void InterpolateHonorsRefsComparer()
        {
            var refs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["Region"] = "us-west-2",
            };
            Assert.AreEqual("us-west-2", Fn.Interpolate("{REGION}", refs));

            var ordinal = new Dictionary<string, object>(StringComparer.Ordinal) { ["Region"] = "r" };
            Assert.AreEqual("r", Fn.Interpolate("{Region}", ordinal));
            Assert.ThrowsExactly<KeyNotFoundException>(() => Fn.Interpolate("{REGION}", ordinal));
        }
    }
}
