using System.Net.Http.Headers;
using System.Web;
using System.Xml;

const string urlBase = "https://hk-modlinks.clazex.net/";
List<string> skipList = new() {
	"clazex.net",
	"vercel.app",
	"jsdelivr.net"
};

List<Task> tasks = new();
try {
	Directory.Delete("dist", true);
} catch { }
Directory.CreateDirectory("dist/apis");
Directory.CreateDirectory("dist/mods");

#region Download and parse ApiLinks.xml

XmlDocument apiLinksXml = new();
apiLinksXml.PreserveWhitespace = true;
using (HttpClient client = new()) {
	apiLinksXml.Load(
		client
			.GetStreamAsync("https://raw.githubusercontent.com/hk-modding/modlinks/main/ApiLinks.xml")
			.Result
	);
}

#endregion

#region Download and parse ModLinks.xml

XmlDocument modLinksXml = new();
modLinksXml.PreserveWhitespace = true;
using (HttpClient client = new()) {
	modLinksXml.Load(
		client
			.GetStreamAsync("https://raw.githubusercontent.com/hk-modding/modlinks/main/ModLinks.xml")
			.Result
	);
}

#endregion

#region Download Apis

#pragma warning disable CS8600, CS8601, CS8602

XmlNode apiLinksNode = apiLinksXml.GetElementsByTagName("Links")[0];
XmlNode linuxNode = apiLinksNode["Linux"].ChildNodes[1];
XmlNode macNode = apiLinksNode["Mac"].ChildNodes[1];
XmlNode windowsNode = apiLinksNode["Windows"].ChildNodes[1];

XmlNode[] apiLinkNodes = { linuxNode, macNode, windowsNode };

IEnumerable<Task> apiDownloadTasks = apiLinkNodes
	.Select(node => new HttpClient()
		.GetStreamAsync(node.InnerText)
		.ContinueWith(task => {
			node.InnerText = urlBase + $"apis/{node.ParentNode.Name}.zip";

			Stream res = task.Result;
			Stream fileStream = File.Create($"dist/apis/{node.ParentNode.Name}.zip");
			res.CopyTo(fileStream);
			fileStream.Dispose();
			res.Dispose();
		})
		.ContinueWith(_ => Console.WriteLine($"Downloaded {node.ParentNode.Name} api"))
	);

tasks = tasks.Concat(apiDownloadTasks).ToList();

#pragma warning restore CS8600, CS8601, S8602

#endregion

#region Download mods

foreach (XmlNode modInfo in modLinksXml.GetElementsByTagName("Manifest")) {

#pragma warning disable CS8600, CS8602

	string name = modInfo["Name"].InnerText;

	XmlNode linkNode = null;
	foreach (XmlNode node in modInfo["Link"].ChildNodes) {
		if (node.Name == "#cdata-section") {
			linkNode = node;
			break;
		}
	}
	string link = linkNode.InnerText;

	if (skipList.Exists(i => link.Contains(i))) {
		continue;
	}

#pragma warning restore CS8600, CS8602

	Task downloadModtask = new HttpClient()
		.GetAsync(link)
		.ContinueWith(task => {
			HttpResponseMessage res = task.Result;

			ContentDispositionHeaderValue? diposition = res.Content.Headers.ContentDisposition;
			string fileName = diposition?.FileName ?? link[(link.LastIndexOf('/') + 1)..];
			fileName = fileName.Trim();
			fileName = fileName.StartsWith('"') ? fileName[1..] : fileName;
			fileName = fileName.EndsWith('"') ? fileName[0..(fileName.Length - 1)] : fileName;
			foreach (char c in Path.GetInvalidFileNameChars()) {
				fileName = fileName.Replace(c, '_');
			}
			linkNode.InnerText = urlBase + $"mods/{HttpUtility.HtmlEncode(fileName)}";

			Stream resStream = res.Content.ReadAsStream();
			using (Stream modFile = File.Create($"dist/mods/{fileName}")) {
				resStream.CopyTo(modFile);
			}
			resStream.Dispose();
		})
		.ContinueWith(_ => Console.WriteLine($"Downloaded {name}"));

	tasks.Add(downloadModtask);
}

#endregion

foreach (Task task in tasks) {
	task.Wait();
}

apiLinksXml.Save("dist/ApiLinks.xml");
modLinksXml.Save("dist/ModLinks.xml");

File.WriteAllText("dist/UPDATE_TIME", DateTime.UtcNow.ToString("s") + "Z\n");
