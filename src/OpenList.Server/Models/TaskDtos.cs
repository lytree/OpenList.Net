using System.Text.Json.Serialization;

namespace OpenList.Server.Models;

public class TaskIdReq { public string Id { get; set; } = ""; }
public class TaskIdsReq { public List<string>? Ids { get; set; } }
