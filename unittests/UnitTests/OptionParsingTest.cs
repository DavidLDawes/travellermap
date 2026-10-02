using Maps;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.IO;

namespace UnitTests
{
    [TestClass]
    public class OptionParsingTest
    {
        private class FakeRequest : Maps.Web.HttpRequest
        {
            public FakeRequest(string query) { QueryString = ParseQueryString(query); }
            public override NameValueCollection QueryString { get; }
            public override NameValueCollection Form { get; } = new NameValueCollection();
            public override Maps.Web.HttpFileCollection Files { get; } = new Maps.Web.HttpFileCollection();
            public override NameValueCollection Headers { get; } = new NameValueCollection();
            public override string HttpMethod => "GET";
            public override string Path => "/api/test";
            public override string RawUrl => Path;
            public override Uri Url => new Uri("http://localhost/api/test");
            public override bool IsLocal => true;
            public override bool IsSecureConnection => false;
            public override Stream InputStream => Stream.Null;
        }

        private static Maps.Web.HttpRequest Request(string query) => new FakeRequest(query);

        // Query strings parse as System.Web parsed them (the handlers were written against it).
        [TestMethod]
        public void QueryStringParsing()
        {
            var q = Maps.Web.HttpRequest.ParseQueryString("?a=1&b=x+y%2Bz&sector=Spinward%20Marches&flag&other&A=2");
            Assert.AreEqual("1,2", q["a"], "repeated keys (case-insensitive) are comma-joined");
            Assert.AreEqual("x y+z", q["b"], "'+' is a space; %2B is a plus");
            Assert.AreEqual("Spinward Marches", q["sector"]);
            Assert.AreEqual("flag,other", q[null], "parameters without '=' go under the null key");
            Assert.IsNull(q["missing"]);
            Assert.AreEqual("", Maps.Web.HttpRequest.ParseQueryString("e=")["e"]);
            Assert.AreEqual(0, Maps.Web.HttpRequest.ParseQueryString("").Count);
        }

        private static readonly IDictionary<string, object> RouteDefaults =
            new Dictionary<string, object> { { "type", "SecondSurvey" }, { "metadata", "0" }, { "jump", 0 } };

        [TestMethod]
        public void StringOptions()
        {
            var r = Request("sector=Spinward%20Marches&type=TabDelimited");
            Assert.AreEqual("Spinward Marches", HandlerBase.GetStringOption(r, RouteDefaults, "sector"));
            Assert.AreEqual("TabDelimited", HandlerBase.GetStringOption(r, RouteDefaults, "type"), "request overrides route default");
            Assert.AreEqual("0", HandlerBase.GetStringOption(r, RouteDefaults, "metadata"), "route default");
            Assert.AreEqual("0", HandlerBase.GetStringOption(r, RouteDefaults, "jump"), "non-string route default");
            Assert.AreEqual("dflt", HandlerBase.GetStringOption(Request("a=1"), null, "sector", "dflt"));

            Assert.IsTrue(HandlerBase.HasOption(r, RouteDefaults, "sector"));
            Assert.IsTrue(HandlerBase.HasOption(r, RouteDefaults, "metadata"));
            Assert.IsFalse(HandlerBase.HasOption(Request("a=1"), null, "sector"));
        }

        [TestMethod]
        public void BoolOptions()
        {
            Assert.IsTrue(HandlerBase.GetBoolOption(Request("po=1"), null, "po", false));
            Assert.IsTrue(HandlerBase.GetBoolOption(Request("po=2"), null, "po", false), "any non-zero integer");
            Assert.IsFalse(HandlerBase.GetBoolOption(Request("routes=0"), null, "routes", true));
            Assert.IsFalse(HandlerBase.GetBoolOption(Request("a=1"), RouteDefaults, "metadata", true), "route default");

            // A bare flag means true (used by the admin pages, e.g. /admin/errors?hide-uwp).
            Assert.IsTrue(HandlerBase.GetBoolOption(Request("hide-uwp"), null, "hide-uwp", false));
            Assert.IsTrue(HandlerBase.GetBoolOption(Request("sector=spin&hide-uwp&hide-tl"), null, "hide-tl", false));
            Assert.IsFalse(HandlerBase.GetBoolOption(Request("hide-uwp"), null, "hide-tl", false));

            // Unparseable values and absent options use the default.
            Assert.IsTrue(HandlerBase.GetBoolOption(Request("po=yes"), null, "po", true));
            Assert.IsFalse(HandlerBase.GetBoolOption(Request("po=yes"), null, "po", false));
            Assert.IsTrue(HandlerBase.GetBoolOption(Request(""), null, "po", true));
        }
    }
}
