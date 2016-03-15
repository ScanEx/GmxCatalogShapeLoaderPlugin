using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using GeoMixerPlugins;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Configuration;
using System.IO;
using CommonWebUtil;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;

namespace ShapeDownloaderPlugin
{
    public class ShapeDownloader : IPlugin
    {
        public const string PluginName = "ShapeDownloader";
        static readonly string DownloadedImagesCatalog = ConfigurationManager.AppSettings["DownloadedImagesCatalog"];
        public bool Start(IGeoMixerInfo geoMixerInfo)
        {
            if (!geoMixerInfo.VersionPluginApi().StartsWith("1."))
            {
                return false;
            }
            GeomixerEvents.ShapeFileRequest.BeforeCreateArchive += BeforeCreateArchive;
            return true;
        }

        public bool Stop()
        {
            GeomixerEvents.ShapeFileRequest.BeforeCreateArchive -= BeforeCreateArchive;
            return true;
        }

        public bool SupportMethod(string methodName)
        {
            switch (methodName)
            {
                case "Name":
                case "Author":
                case "Version":
                case "State":
                    return true;
            }
            return false;
        }

        public object CallMethod(string methodName, object param)
        {
            switch (methodName)
            {
                case "Name":
                    return PluginName;
                case "Author":
                    return "Scanex";
                case "Version":
                    return "1.0";
                case "State":
                    return "active";
            }
            return null;
        }

        void BeforeCreateArchive(object sender, ShapeFileZipEvent args)
        {
            var ctx = args.Context as HttpContext;
            if (ctx != null && ctx.Request.Form["Request"] != null)
            {
                var jo = JObject.Parse(ctx.Request.Form["Request"]);
                bool useImages = false;
                if (jo["Images"] != null && bool.TryParse(jo["Images"].ToString(), out useImages) && useImages)
                {
                    IEnumerable<LayerData.Structures.VectorFile> contours = args.Archive.Files.Where(x => x.FileName == "contours");
                    foreach (LayerData.Structures.VectorFile vectorFile in args.Archive.Files.Except(contours))
                    {
                        foreach (GeometryUtil.GeometryFeature f in vectorFile.Features)
                        {
                            object id, sat_name, date;
                            DateTime acqdate = new DateTime(0, DateTimeKind.Utc);
                            if (
                               f.Properties.TryGetValue("id", out id) &&
                               f.Properties.TryGetValue("sat_name", out sat_name) &&
                               f.Properties.TryGetValue("date", out date) &&
                               DateTime.TryParse(date.ToString(), out acqdate))
                            {
                                string sceneid = id.ToString();
                                string platform = sat_name.ToString();
                                var dir = new DirectoryInfo(
                                    Path.Combine(
                                    DownloadedImagesCatalog,
                                    platform.ToLower(),
                                    acqdate.Year.ToString(),
                                    acqdate.Month.ToString(),
                                    GetDayDirName(acqdate)));

                                var filePath = new FileInfo(Path.ChangeExtension(Path.Combine(dir.ToString(), sceneid), ".jpg"));
                                var imgFile = Path.ChangeExtension(Path.Combine(args.Archive.TempDirectory.FullName, sceneid), ".jpg");
                                if (!filePath.Exists)
                                {
                                    if (!dir.Exists)
                                    {
                                        dir.Create();
                                    }
                                    object url;
                                    if (f.Properties.TryGetValue("url", out url))
                                    {
                                        var buf = WebHelper.ReadContent(url.ToString(), 60);
                                        var strImage = new MemoryStream(buf);
                                        var img = Image.FromStream(strImage);
                                        File.WriteAllBytes(filePath.FullName, buf);
                                        WriteFiles(filePath.FullName,
                                            imgFile,
                                            platform,
                                            img.Height, img.Width, f.Properties);
                                    }
                                }
                                else
                                {
                                    var buf = File.ReadAllBytes(filePath.FullName);
                                    var strImage = new MemoryStream(buf);
                                    var img = Image.FromStream(strImage);

                                    WriteFiles(
                                        filePath.FullName,
                                        imgFile,
                                        platform,
                                        img.Height, img.Width, f.Properties);

                                }
                            }
                        }
                    }
                }
            }
        }

        static void WriteFiles(
            string file,
            string imgFile,
            string platform,
            int height,
            int width,
            Dictionary<string, object> properties)
        {
            File.Copy(file, imgFile);

            object x1, y1, x2, y2, x3, y3, x4, y4;
            if (properties.TryGetValue("x1", out x1) && properties.TryGetValue("y1", out y1) &&
                properties.TryGetValue("x2", out x2) && properties.TryGetValue("y2", out y2) &&
                properties.TryGetValue("x3", out x3) && properties.TryGetValue("y3", out y3) &&
                properties.TryGetValue("x4", out x4) && properties.TryGetValue("y4", out y4))
            {
                double _x1 = Convert.ToDouble(x1), _y1 = Convert.ToDouble(y1),
                    _x2 = Convert.ToDouble(x2), _y2 = Convert.ToDouble(y2),
                    _x3 = Convert.ToDouble(x3), _y3 = Convert.ToDouble(y3),
                    _x4 = Convert.ToDouble(x4), _y4 = Convert.ToDouble(y4);

                WriteAnchors(
                    platform, imgFile,
                    _x1, _y1,
                    _x2, _y2,
                    _x3, _y3,
                    _x4, _y4, height, width);
            }
        }

        static void WriteAnchors(
            string sat_name,
            string file,
            double _x1, double _y1,
            double _x2, double _y2,
            double _x3, double _y3,
            double _x4, double _y4,
            int height, int width,
            string formatExt = ".jgw")
        {                        

            //http://gis-lab.info/qa/tfw.html
            //x1 = Ax + Сy + E
            //y1 = Dx + By + F
            //x,y - исходные файловые координаты растра (x - колонка, y - ряд).
            //x1,y1 - долгота/широта
            double E = _x1;
            double F = _y1;
            double A = (_x2 - E) / width;
            double B = (_y4 - F) / height;
            double C = (_x4 - E) / height;
            double D = (_y2 - F) / width;
            string world = string.Join("\r\n", new[] { A, C, D, B, E, F }.Select(x => x.ToString(CultureInfo.InvariantCulture)));
            File.WriteAllText(Path.ChangeExtension(file, formatExt), world, Encoding.Default);

            //AUX
            string aux = "<PAMDataset><SRS>GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]],TOWGS84[0,0,0,0,0,0,0],AUTHORITY[\"EPSG\",\"6326\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.0174532925199433,AUTHORITY[\"EPSG\",\"9108\"]],AUTHORITY[\"EPSG\",\"4326\"]]/SRS></PAMDataset>";
            File.WriteAllText(Path.ChangeExtension(file, ".aux.xml"), aux, Encoding.Default);

            // mapinfo
            string mi =
string.Format(CultureInfo.InvariantCulture, @"!table
!version 300
!Charset WindowsLatin1

Definition Table
  File ""{11}""
  Type ""RASTER""
({0},{1}) (0,0) Label ""UpLeft"",
({2},{3}) ({8},0) Label ""UpRight"",
({4},{5}) ({8},{9}) Label ""BottRight"",
({6},{7}) (0,{9}) Label ""BottLeft""
 CoordSys {10}", _x1, _y1, _x2, _y2, _x3, _y3, _x4, _y4, width, height, "Earth Projection 1, 0", Path.GetFileName(file));
            File.WriteAllText(Path.ChangeExtension(file, ".tab"), mi, Encoding.Default);

            // kml            
            string kml =
string.Format(CultureInfo.InvariantCulture, @"<?xml version=""1.0"" encoding=""UTF-8""?>
<kml xmlns=""http://www.opengis.net/kml/2.2"" xmlns:gx=""http://www.google.com/kml/ext/2.2"" xmlns:kml=""http://www.opengis.net/kml/2.2"" xmlns:atom=""http://www.w3.org/2005/Atom"">
<GroundOverlay>
<name>{8}</name>
<Icon>
<href>{8}</href>
</Icon>
<gx:LatLonQuad>
	<coordinates>
		{0},{1},0 {2},{3},0 {4},{5},0 {6},{7},0 
	</coordinates>
</gx:LatLonQuad>
</GroundOverlay>
</kml>", _x4, _y4, _x3, _y3, _x2, _y2, _x1, _y1, Path.GetFileName(file));
            File.WriteAllText(Path.ChangeExtension(file, ".kml"), kml, Encoding.UTF8);
        }

        static string GetDayDirName(DateTime date)
        {
            string dateDirName = null;
            if (1 <= date.Day && date.Day <= 10) dateDirName = "1-10";
            if (11 <= date.Day && date.Day <= 20) dateDirName = "11-20";
            if (21 <= date.Day && date.Day <= 31) dateDirName = "21-31";
            return dateDirName;
        }
    }
}
