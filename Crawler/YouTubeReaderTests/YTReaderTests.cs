using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SysExtensions.Collections;
using SysExtensions.Fluent.IO;
using SysExtensions.Serialization;
using YouTubeReader;

namespace YouTubeReaderTests
{
    [TestClass]
    public class YTReaderTests
    {
        [TestMethod]
        public async Task SaveTrendingCsvTest()
        {
            //var yt = new YTReader();
            //await yt.SaveTrendingCsv();
        }

        [TestMethod]
        public void DropChannels() {
            var db = Setup.Db();
            db.DropCollection("Channels");
        }
      

        [TestMethod]
        public void NotNullTest() {
            IEnumerable<int> list = null;
            var res = list.NotNull();
        }
    }
}
