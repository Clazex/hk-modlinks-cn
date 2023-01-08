using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
try {
	Directory.Delete("temp", true);
} catch { }

Directory.CreateDirectory("dist/apis");
Directory.CreateDirectory("dist/mods");
Directory.CreateDirectory("temp");

#region Download and parse ApiLinks.xml

XmlDocument apiLinksXml = new() {
	PreserveWhitespace = true
};
using (HttpClient client = new()) {
	apiLinksXml.Load(
		client
			.GetStreamAsync("https://raw.githubusercontent.com/hk-modding/modlinks/main/ApiLinks.xml")
			.Result
	);
}

#endregion

#region Download and parse ModLinks.xml

XmlDocument modLinksXml = new() {
	PreserveWhitespace = true
};
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

			Console.WriteLine($"Downloaded {node.ParentNode.Name} api");
		})
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

	if (skipList.Exists(link.Contains)) {
		continue;
	}

#pragma warning restore CS8600, CS8602

	Task downloadModtask = new HttpClient()
		.GetStreamAsync(link)
		.ContinueWith(task => {
			string modName = Regex.Replace(name.Normalize(NormalizationForm.FormD), @"[^ -&(-_a-~]", "");
			StringBuilder modNameBuilder = new(modName.Length);
			Regex.Matches(modName, @"(?:[A-Z]?[a-z]+)|[A-Z]|\d+")
				.Select(match => match.Value)
				.ToList()
				.ForEach(part => {
					_ = modNameBuilder.Append(char.ToUpperInvariant(part[0]));

					if (part.Length > 1) {
						_ = modNameBuilder.Append(part[1..]);
					}
				});

			modName = modNameBuilder.ToString();

			if (!char.IsUpper(modName[0])) {
				modName = modName.Length switch {
					1 => char.ToUpperInvariant(modName[0]) + modName[1..],
					_ => modName.ToUpperInvariant()
				};
			}

			string version = modInfo["Version"]!.InnerText;
			string fullModName = $"{modName}-v{version}";
			string fileName = fullModName + ".zip";

			linkNode.InnerText = urlBase + $"mods/{HttpUtility.HtmlEncode(fileName)}";

			Stream resStream = task.Result;
			using MemoryStream ms = new();
			resStream.CopyTo(ms);
			resStream.Dispose();

			try {
				_ = new ZipArchive(ms, ZipArchiveMode.Read, true);

				ms.Position = 0;
				using Stream modFile = File.Create($"dist/mods/{fileName}");
				ms.CopyTo(modFile);
			} catch (InvalidDataException) {
				_ = Directory.CreateDirectory($"temp/{modName}");
				using (Stream tempFile = File.Create($"temp/{modName}/{modName}.dll")) {
					ms.CopyTo(tempFile);
				}

				ZipFile.CreateFromDirectory(
					$"temp/{modName}",
					$"dist/mods/{fileName}",
					CompressionLevel.SmallestSize,
					false
				);

				Console.WriteLine($"Compressed {name}");
			}

			if (name == modName) {
				Console.WriteLine($"Downloaded {fullModName}");
			} else {
				Console.WriteLine($"Downloaded {name} as {fullModName}");
			}
		});

	tasks.Add(downloadModtask);
}

#endregion

await Task.WhenAll(tasks);

Directory.Delete("temp", true);
apiLinksXml.Save("dist/ApiLinks.xml");
modLinksXml.Save("dist/ModLinks.xml");

File.WriteAllText("dist/UPDATE_TIME", DateTime.UtcNow.ToString("s") + "Z\n");
