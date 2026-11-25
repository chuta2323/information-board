/** @file BoardController.cs
 * @brief IIS上で動作する同一オリジン専用のWeb API。
 */
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web;
using System.Web.Http;
using Newtonsoft.Json.Serialization;

namespace InfoBoard {
  /** @brief ASP.NETアプリケーションの初期化。 */
  public class BoardApplication : HttpApplication {
    /** @brief JSON APIと属性ルーティングを有効にする。 */
    protected void Application_Start() {
      GlobalConfiguration.Configure(config => {
        config.MapHttpAttributeRoutes();
        config.Formatters.Remove(config.Formatters.XmlFormatter);
        config.Formatters.JsonFormatter.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
        config.IncludeErrorDetailPolicy = IncludeErrorDetailPolicy.Never;
      });
    }
  }

  /** @brief 保存済み情報の取得と操作を提供する。 */
  [RoutePrefix("api/board")]
  public sealed class BoardController : ApiController {
    private static readonly Lazy<BoardStore> STORE = new Lazy<BoardStore>(() => new BoardStore(Path.Combine(HttpRuntime.AppDomainAppPath, "App_Data", "board.db")));

    /** @brief APIのレスポンスを生成しキャッシュを禁止する。
     * @param [in] operation 操作本体
     * @return HTTPレスポンス
     */
    private HttpResponseMessage Handle(Func<HttpResponseMessage> operation) {
      HttpResponseMessage response;
      try {
        response = operation();
      } catch (BoardError error) {
        response = Request.CreateResponse((HttpStatusCode)error.Status, new { message = error.Message });
      } catch (Exception error) {
        Trace.TraceError("InfoBoard: {0}", error);
        response = Request.CreateResponse(HttpStatusCode.InternalServerError, new { message = "サーバー処理に失敗しました。管理者へ連絡してください。" });
      }
      response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
      return response;
    }

    /** @brief 最新のボードを取得する。
     * @return HTTPレスポンス
     */
    [HttpGet, Route("")]
    public HttpResponseMessage Read() => Handle(() => Request.CreateResponse(HttpStatusCode.OK, STORE.Value.Read()));

    /** @brief JSON形式の変更要求を処理する。
     * @param [in] input 変更要求
     * @return HTTPレスポンス
     * @note 独自ヘッダーと同一オリジン制約でブラウザーからのCSRFを防止する。
     */
    [HttpPost, Route("")]
    public HttpResponseMessage Change([FromBody] InfoBoard.Request input) => Handle(() => {
      if (!Request.Headers.TryGetValues("X-InfoBoard", out var values) || !values.SequenceEqual(new[] { "1" }) || Request.Content?.Headers.ContentType?.MediaType != "application/json") {
        throw new BoardError(403, "不正な変更要求です。");
      }
      if (Request.Headers.TryGetValues("Origin", out var origins) && (origins.Count() != 1 || !string.Equals(origins.First(), Request.RequestUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))) {
        throw new BoardError(403, "異なるオリジンからの操作は許可されていません。");
      }
      if (!ModelState.IsValid) throw new BoardError(400, "入力形式が正しくありません。");
      return Request.CreateResponse(HttpStatusCode.OK, STORE.Value.Apply(input));
    });

    /** @brief アーカイブを含む全カードをCSVで返す。
     * @return ダウンロードレスポンス
     */
    [HttpGet, Route("export")]
    public HttpResponseMessage Export() => Handle(() => {
      var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(STORE.Value.Export()) };
      response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/csv") { CharSet = "utf-8" };
      response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "InfoBoard_" + DateTime.UtcNow.AddHours(9).ToString("yyyyMMdd_HHmm") + ".csv" };
      return response;
    });
  }
}
