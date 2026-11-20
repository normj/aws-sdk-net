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
using Amazon.Runtime.Internal.Endpoints.StandardLibrary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace AWSSDK.UnitTests
{
    [TestClass]
    public class EndpointsSubstringTests
    {
        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        [DataRow("my-application-bucket", 0, 6, true, "bucket")]
        [DataRow("my-application-bucket", 0, 6, false, "my-app")]
        [DataRow("abc--x-s3", 0, 6, true, "--x-s3")]
        [DataRow("abcdef", 0, 6, false, "abcdef")]
        [DataRow("abcdef", 2, 4, true, "cd")]
        [DataRow("abcde", 0, 6, false, null)]
        [DataRow("abcdef", 3, 3, false, null)]
        [DataRow("abcdef", 4, 3, false, null)]
        [DataRow("", 0, 1, false, null)]
        [DataRow("abc\u007f", 0, 2, false, "ab")]
        [DataRow("abc\u0080", 0, 2, false, null)]
        [DataRow("\u00e9abcdef", 0, 2, true, null)]
        [DataRow("abcdef\ud83d\ude00", 0, 2, false, null)]
        public void Substring(string input, int start, int stop, bool reverse, string expected)
        {
            Assert.AreEqual(expected, Fn.Substring(input, start, stop, reverse));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void SubstringNullInputThrows()
        {
            Assert.ThrowsExactly<NullReferenceException>(() => Fn.Substring(null, 0, 1, false));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void SubstringRejectsAnyNonAsciiCharacter()
        {
            for (int c = 0; c <= 0xFFFF; c++)
            {
                var input = "abc" + (char)c + "def";
                var result = Fn.Substring(input, 0, 2, false);
                if (c > 127)
                {
                    Assert.IsNull(result, "char " + c);
                }
                else
                {
                    Assert.AreEqual("ab", result, "char " + c);
                }
            }
        }
    }
}
