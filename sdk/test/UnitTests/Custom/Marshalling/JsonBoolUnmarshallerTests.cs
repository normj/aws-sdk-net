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
using Amazon.Runtime.Internal.Transform;
using Amazon.Runtime.Internal.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins what BoolUnmarshaller and NullableBoolUnmarshaller return for every JSON token shape.
    /// </summary>
    [TestClass]
    public class JsonBoolUnmarshallerTests
    {
        private static bool Bool(string json)
        {
            using (var context = new JsonUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes("[" + json + "]")), false, null))
            {
                // Values are read inside an array, as generated code always reads them inside a document.
                var reader = new StreamingUtf8JsonReader(context.Stream);
                context.Read(ref reader); // [
                return BoolUnmarshaller.Instance.Unmarshall(context, ref reader);
            }
        }

        private static bool? NullableBool(string json)
        {
            using (var context = new JsonUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes("[" + json + "]")), false, null))
            {
                var reader = new StreamingUtf8JsonReader(context.Stream);
                context.Read(ref reader); // [
                return NullableBoolUnmarshaller.Instance.Unmarshall(context, ref reader);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void BoolTokens()
        {
            Assert.IsTrue(Bool("true"));
            Assert.IsFalse(Bool("false"));
            Assert.IsTrue(Bool(" true "));
            Assert.IsFalse(Bool("null"));
            Assert.IsTrue(Bool("\"true\""));
            Assert.IsTrue(Bool("\"True\""));
            Assert.IsFalse(Bool("\"FALSE\""));
            Assert.ThrowsExactly<FormatException>(() => Bool("\"yes\""));
            Assert.ThrowsExactly<FormatException>(() => Bool("\"\""));
            Assert.ThrowsExactly<FormatException>(() => Bool("1"));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void NullableBoolTokens()
        {
            Assert.AreEqual(true, NullableBool("true"));
            Assert.AreEqual(false, NullableBool("false"));
            Assert.IsNull(NullableBool("null"));
            Assert.IsNull(NullableBool("\"\""));
            Assert.AreEqual(true, NullableBool("\"true\""));
            Assert.AreEqual(false, NullableBool("\"False\""));
            Assert.ThrowsExactly<FormatException>(() => NullableBool("\"yes\""));
            Assert.ThrowsExactly<FormatException>(() => NullableBool("0"));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void BoolListAndContextState()
        {
            var json = "{\"A\":[true,false,null,\"true\"],\"B\":true,\"C\":1}";
            using (var context = new JsonUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(json)), false, null))
            {
                var reader = new StreamingUtf8JsonReader(context.Stream);
                context.Read(ref reader); // {
                context.Read(ref reader); // A
                Assert.IsTrue(context.TestExpression("A", 1, ref reader));
                var list = new JsonListUnmarshaller<bool?, NullableBoolUnmarshaller>(NullableBoolUnmarshaller.Instance).Unmarshall(context, ref reader);
                CollectionAssert.AreEqual(new bool?[] { true, false, null, true }, list);
                context.Read(ref reader); // B
                Assert.IsTrue(context.TestExpression("B", 1, ref reader));
                Assert.IsTrue(BoolUnmarshaller.Instance.Unmarshall(context, ref reader));
                Assert.AreEqual(1, context.CurrentDepth);
                Assert.AreEqual("/", context.CurrentPath);
                context.Read(ref reader); // C
                Assert.IsTrue(context.TestExpression("C", 1, ref reader));
                Assert.AreEqual("/C", context.CurrentPath);
            }
        }
    }
}
