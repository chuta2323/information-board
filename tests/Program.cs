/** @file Program.cs
 * @brief 一時SQLiteを使った永続化、競合、履歴、CSVのスモークテスト。
 */
using System;
using System.IO;
using System.Linq;
using System.Text;
using InfoBoard;
using Newtonsoft.Json.Linq;

/** @brief テスト実行エントリーポイント。 */
internal static class Program {
  /** @brief 条件が成立しなければテストを失敗させる。
   * @param [in] condition 期待条件
   * @param [in] message 失敗時の説明
   */
  private static void Check(bool condition, string message) {
    if (!condition) throw new Exception(message);
  }

  /** @brief 競合による拒否を確認する。
   * @param [in] action 競合する操作
   */
  private static void Conflict(Action action) {
    try { action(); } catch (BoardError error) {
      Check(error.Status == 409, "expected 409");
      return;
    }
    throw new Exception("conflicting operation succeeded");
  }

  /** @brief 操作結果からトークンを取得する。
   * @param [in] value 操作結果
   * @return トークン
   */
  private static string Token(object value) => (string)JObject.FromObject(value)["token"];

  /** @brief DB操作を一連のシナリオで検証する。
   * @return テスト終了コード
   */
  private static int Main() {
    string directory = Path.Combine(Path.GetTempPath(), "InfoBoard-test-" + Guid.NewGuid().ToString("N"));
    try {
      long now = 1770000000;
      string path = Path.Combine(directory, "board.db");
      var first = new BoardStore(path, () => now);
      var second = new BoardStore(path, () => now);
      var initial = new Content { Title = "連絡事項", Owner = "山田", Body = "## 見出し\n本文,\"引用\"", Color = "blue" };
      first.Apply(new Request { Action = "create", Content = initial });
      var card = first.Read().Cards.Single();
      string id = card.Id;
      string token = Token(first.Apply(new Request { Action = "acquire", Id = id }));
      Check(second.Read().Cards.Single().Editing, "editing state");
      Conflict(() => second.Apply(new Request { Action = "acquire", Id = id }));
      Conflict(() => second.Apply(new Request { Action = "archive", Id = id, Version = card.Version }));
      Conflict(() => second.Apply(new Request { Action = "save", Id = id, Token = "wrong", Version = card.Version, Content = initial }));
      now += 50;
      first.Apply(new Request { Action = "heartbeat", Id = id, Token = token });
      now += 20;
      Check(second.Read().Cards.Single().Editing, "heartbeat extension");
      var changed = new Content { Title = "更新", Owner = "佐藤", Body = "=1+1", Color = "green" };
      first.Apply(new Request { Action = "save", Id = id, Token = token, Version = card.Version, Content = changed });
      card = second.Read().Cards.Single();
      Check(!card.Editing && card.HasPrevious && card.Title == "更新", "save and release");
      second.Apply(new Request { Action = "restore", Id = id, Version = card.Version });
      card = first.Read().Cards.Single();
      Check(card.Title == initial.Title && card.Body == initial.Body && card.Color == initial.Color, "restore");
      second.Apply(new Request { Action = "restore", Id = id, Version = card.Version });
      Check(first.Read().Cards.Single().Title == changed.Title, "restore swap");
      token = Token(first.Apply(new Request { Action = "acquire", Id = id }));
      now += 60;
      Check(!second.Read().Cards.Single().Editing, "lease expires");
      string newToken = Token(second.Apply(new Request { Action = "acquire", Id = id }));
      Conflict(() => first.Apply(new Request { Action = "save", Id = id, Token = token, Version = card.Version, Content = initial }));
      second.Apply(new Request { Action = "release", Id = id, Token = newToken });
      first.Apply(new Request { Action = "create", Content = initial });
      var snapshot = second.Read();
      string sortToken = Token(first.Apply(new Request { Action = "sortAcquire" }));
      Conflict(() => second.Apply(new Request { Action = "sortAcquire" }));
      Conflict(() => second.Apply(new Request { Action = "create", Content = initial }));
      var order = snapshot.Cards.Select(c => c.Id).Reverse().ToArray();
      first.Apply(new Request { Action = "sortSave", Token = sortToken, Version = snapshot.OrderVersion, Order = order });
      Check(second.Read().Cards.Select(c => c.Id).SequenceEqual(order), "reorder");
      card = first.Read().Cards.First(c => c.Id == id);
      first.Apply(new Request { Action = "archive", Id = id, Version = card.Version });
      var restarted = new BoardStore(path, () => now);
      card = restarted.Read().Cards.Single(c => c.Id == id);
      Check(card.Archived && card.Title == changed.Title, "archive persistence");
      var csv = restarted.Export();
      Check(csv.Take(3).SequenceEqual(new byte[] {239, 187, 191}), "CSV BOM");
      string text = Encoding.UTF8.GetString(csv);
      Check(text.Contains("アーカイブ") && text.Contains("'=1+1") && text.Contains("本文,"), "CSV all cards and safe values");
      Check(BoardStore.CsvField("a\"b\n,c") == "\"a\"\"b\n,c\"", "CSV quoting");
      restarted.Apply(new Request { Action = "unarchive", Id = id, Version = card.Version });
      Check(!first.Read().Cards.Last().Archived && first.Read().Cards.Last().Id == id, "unarchive at end");
      card = first.Read().Cards.Single(c => c.Id == id);
      Conflict(() => first.Apply(new Request { Action = "delete", Id = id, Version = card.Version }));
      first.Apply(new Request { Action = "archive", Id = id, Version = card.Version });
      card = first.Read().Cards.Single(c => c.Id == id);
      Conflict(() => second.Apply(new Request { Action = "delete", Id = id, Version = card.Version - 1 }));
      sortToken = Token(first.Apply(new Request { Action = "sortAcquire" }));
      Conflict(() => second.Apply(new Request { Action = "delete", Id = id, Version = card.Version }));
      first.Apply(new Request { Action = "sortRelease", Token = sortToken });
      second.Apply(new Request { Action = "delete", Id = id, Version = card.Version });
      Check(first.Read().Cards.All(c => c.Id != id), "permanent deletion");
      Check(!Encoding.UTF8.GetString(first.Export()).Contains(id), "deleted card excluded from CSV");
      Check(first.Read().Cards.Single().Body == initial.Body, "remaining content unchanged");
      Console.WriteLine("InfoBoard SQLite smoke tests passed.");
      return 0;
    } finally {
      if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
    }
  }
}
