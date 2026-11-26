/** @file TextStoreSmoke.cs
 * @brief テキスト配備版のファイル保存・排他・カード操作をWindows上で検証する。
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

internal static class TextStoreSmoke {
  private static void Check(bool condition, string label) {
    if (!condition) throw new Exception(label);
  }

  private static void Reject(int status, Action operation) {
    try { operation(); } catch (BoardError error) {
      Check(error.Status == status, "wrong status: " + error.Status);
      return;
    }
    throw new Exception("operation unexpectedly succeeded");
  }

  private static Dictionary<string, object> Object(object value) {
    return BoardStore.Serializer().Deserialize<Dictionary<string, object>>(BoardStore.Serializer().Serialize(value));
  }

  private static Dictionary<string, object>[] Cards(BoardStore store) {
    return BoardStore.Serializer().Deserialize<Dictionary<string, object>[]>(BoardStore.Serializer().Serialize(Object(store.Read())["cards"]));
  }

  private static string Id(Dictionary<string, object> card) { return (string)card["id"]; }
  private static long Version(Dictionary<string, object> card) { return Convert.ToInt64(card["version"]); }
  private static string Token(object result) { return (string)Object(result)["token"]; }
  private static long OrderVersion(BoardStore store) { return Convert.ToInt64(Object(store.Read())["orderVersion"]); }
  private static CardContent Content(string title, string body) { return new CardContent { title = title, owner = "担当", body = body, color = "blue" }; }

  private static int Main() {
    string directory = Path.Combine(Path.GetTempPath(), "InfoBoard-text-" + Guid.NewGuid().ToString("N"));
    try {
      long now = 1770000000;
      var a = new BoardStore(directory, () => now);
      var b = new BoardStore(directory, () => now);
      Check((string)Object(a.Read())["siteName"] == "InfoBoard", "default name");
      Check(File.Exists(Path.Combine(directory, "board.json")), "initial file");
      string body = "1行目\n\n  字下げ\t空白\n<script>alert(1)</script>";
      a.Apply(new BoardRequest { action = "create", content = Content("連絡", body) });
      var card = Cards(b).Single();
      string id = Id(card);
      Check((string)card["body"] == body, "body unchanged");
      string token = Token(a.Apply(new BoardRequest { action = "acquire", id = id }));
      Check((bool)Cards(b).Single()["editing"], "editing visibility");
      Reject(409, () => b.Apply(new BoardRequest { action = "acquire", id = id }));
      Reject(409, () => b.Apply(new BoardRequest { action = "archive", id = id, version = Version(card) }));
      now += 50;
      a.Apply(new BoardRequest { action = "heartbeat", id = id, token = token });
      now += 20;
      Check((bool)Cards(b).Single()["editing"], "heartbeat");
      a.Apply(new BoardRequest { action = "save", id = id, version = Version(card), token = token, content = Content("更新", "=1+1") });
      card = Cards(b).Single();
      Check((bool)card["hasPrevious"] && !(bool)card["editing"] && !card.ContainsKey("previous"), "history and release");
      b.Apply(new BoardRequest { action = "restore", id = id, version = Version(card) });
      card = Cards(a).Single();
      Check((string)card["body"] == body, "restore");
      token = Token(a.Apply(new BoardRequest { action = "acquire", id = id }));
      now += 60;
      Check(!(bool)Cards(b).Single()["editing"], "lease expires");
      Reject(409, () => a.Apply(new BoardRequest { action = "save", id = id, version = Version(card), token = token, content = Content("失効", "") }));
      a.Apply(new BoardRequest { action = "create", content = Content("別カード", "=1+1") });
      string[] initial = Cards(a).Select(Id).ToArray();
      long orderVersion = OrderVersion(a);
      string sortToken = Token(a.Apply(new BoardRequest { action = "sortAcquire" }));
      Reject(409, () => b.Apply(new BoardRequest { action = "create", content = Content("競合", "") }));
      a.Apply(new BoardRequest { action = "sortSave", token = sortToken, version = orderVersion, order = initial.Reverse().ToArray() });
      Check(Cards(b).Select(Id).SequenceEqual(initial.Reverse()), "reorder");
      card = Cards(a).Single(c => Id(c) == id);
      Reject(409, () => a.Apply(new BoardRequest { action = "delete", id = id, version = Version(card) }));
      a.Apply(new BoardRequest { action = "archive", id = id, version = Version(card) });
      card = Cards(b).Single(c => Id(c) == id);
      Check((bool)card["archived"], "archive");
      a.Apply(new BoardRequest { action = "unarchive", id = id, version = Version(card) });
      card = Cards(b).Last();
      Check(Id(card) == id && !(bool)card["archived"], "unarchive at end");
      a.Apply(new BoardRequest { action = "archive", id = id, version = Version(card) });
      card = Cards(b).Last();
      a.Apply(new BoardRequest { action = "delete", id = id, version = Version(card) });
      Check(Cards(b).Length == 1, "permanent deletion");
      byte[] csv = b.Export();
      Check(csv.Take(3).SequenceEqual(new byte[] {239, 187, 191}), "CSV BOM");
      Check(Encoding.UTF8.GetString(csv).Contains("'=1+1") && !Encoding.UTF8.GetString(csv).Contains(id), "CSV safety and deletion");
      a.Apply(new BoardRequest { action = "renameSite", siteName = "共有ボード", version = 0 });
      Reject(409, () => b.Apply(new BoardRequest { action = "renameSite", siteName = "競合", version = 0 }));
      Check((string)Object(new BoardStore(directory).Read())["siteName"] == "共有ボード", "site name persistence");
      Parallel.For(0, 8, i => new BoardStore(directory).Apply(new BoardRequest { action = "create", content = Content("並行" + i, "") }));
      Check(Cards(b).Length == 9, "concurrent writes");
      Check(File.Exists(Path.Combine(directory, "board.previous.json")), "replacement backup");
      string path = Path.Combine(directory, "board.json");
      File.WriteAllText(path, "broken");
      bool failed = false;
      try { a.Apply(new BoardRequest { action = "create", content = Content("破損", "") }); } catch { failed = true; }
      Check(failed && File.ReadAllText(path) == "broken", "corruption fails closed");
      string legacy = Path.Combine(directory, "legacy");
      Directory.CreateDirectory(legacy);
      File.WriteAllText(Path.Combine(legacy, "board.db"), "legacy");
      Reject(503, () => new BoardStore(legacy).Read());
      Check(!File.Exists(Path.Combine(legacy, "board.json")), "no implicit legacy migration");
      Console.WriteLine("InfoBoard text store smoke tests passed.");
      return 0;
    } finally {
      if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
    }
  }
}
