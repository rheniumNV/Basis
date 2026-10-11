using Basis.Scripts.BasisSdk.Players;
using Cilbox;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Keeps the fishing world's records on the fishing API (the basis-fishing Go service): sends
/// the local player's catches, and fetches the level ranking and the player's own encyclopedia
/// for the boards.
///
/// The player is identified by their Basis id (BasisPlayer.UUID). World scripts are not given it
/// (it would let worlds track people between visits), and neither UnityWebRequest; both are asked
/// for in the script permission prompt. A player who refuses gets a failed call here and Cilbox
/// switches this script off, so it lives on its own and only ever calls into the controller,
/// never the other way round: fishing carries on, just without the server.
///
/// One request at a time, polled from Update (no coroutines in Cilbox). JSON is built and read by
/// hand: no JSON library is available to world scripts.
/// </summary>
[Cilboxable]
public class FishingBackend : MonoBehaviour
{
    private const int KindNone = 0;
    private const int KindCatch = 1;
    private const int KindRanking = 2;
    private const int KindZukan = 3;

    public FishingWorldController Controller;
    // e.g. https://fishing.example.com (no trailing slash). Empty: no server, nothing is sent.
    public string ApiBaseUrl = "";
    public int RankingLimit = 10;
    public float RankingRefreshSeconds = 60f;
    public float RetrySeconds = 10f;
    public int TimeoutSeconds = 10;
    // Editor only: the id to use while offline, where Basis has no player id.
    public string EditorTestUserId = "";

    private string userId;
    private string displayName;
    private UnityWebRequest request;
    private int requestKind;
    private float nextRankingAt;
    private float nextAttemptAt;
    private bool zukanWanted = true;
    private bool zukanLoaded;

    private void Start()
    {
        if (ApiBaseUrl.Length == 0)
        {
            Controller.SetRankingText("<b>レベルランキング</b>\n<color=#7A8A80>準備中</color>");
            return;
        }
        // Touch the permission-gated web API first: if the player refused it, this throws and the
        // script stops here, leaving the boards on their offline text.
        UnityWebRequest probe = new UnityWebRequest();
        probe.Dispose();
    }

    private void Update()
    {
        if (ApiBaseUrl.Length == 0) return;
        Controller.BackendHeartbeat();
        if (request != null)
        {
            if (request.isDone) Finish();
            return;
        }
        // IsNullOrEmpty, not == null: Cilbox does not start unserialized string fields as null.
        if (string.IsNullOrEmpty(userId) && !ResolvePlayer()) return;
        if (Time.time < nextAttemptAt) return;

        if (Controller.PendingCatchCount() > 0)
        {
            SendCatch();
        }
        else if (zukanWanted)
        {
            Begin(KindZukan, UnityWebRequest.kHttpVerbGET, ApiBaseUrl + "/api/v1/users/" + UnityWebRequest.EscapeURL(userId) + "/encyclopedia?limit=500", null);
        }
        else if (Time.time >= nextRankingAt)
        {
            Begin(KindRanking, UnityWebRequest.kHttpVerbGET, ApiBaseUrl + "/api/v1/rankings/levels?limit=" + RankingLimit, null);
        }
    }

    // The local player's Basis id and name, once connected (the id is set by the server login).
    private bool ResolvePlayer()
    {
        BasisPlayer player = BasisLocalPlayer.Instance;
        if (player == null) return false;
        string id = player.UUID;
        if (string.IsNullOrEmpty(id) && Application.isEditor && EditorTestUserId.Length > 0) id = EditorTestUserId;
        if (string.IsNullOrEmpty(id)) return false;
        userId = id;
        displayName = player.SafeDisplayName;
        if (string.IsNullOrEmpty(displayName)) displayName = player.DisplayName;
        if (string.IsNullOrEmpty(displayName)) displayName = "名無しの釣り人";
        return true;
    }

    private void SendCatch()
    {
        StringBuilder json = new StringBuilder();
        json.Append("{\"userId\":");
        AppendJsonString(json, userId);
        json.Append(",\"displayName\":");
        AppendJsonString(json, displayName);
        json.Append(",\"fishName\":");
        AppendJsonString(json, Controller.PendingFishName());
        json.Append(",\"size\":");
        json.Append(FormatDecimal(Controller.PendingSizeCm()));
        json.Append(",\"experienceGained\":");
        json.Append(Controller.PendingExp());
        json.Append(",\"moneyGained\":");
        json.Append(Controller.PendingMoney());
        json.Append("}");
        Begin(KindCatch, UnityWebRequest.kHttpVerbPOST, ApiBaseUrl + "/api/v1/catches", json.ToString());
    }

    private void Begin(int kind, string method, string url, string body)
    {
        request = new UnityWebRequest(url, method);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.timeout = TimeoutSeconds;
        if (body != null)
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.SetRequestHeader("Content-Type", "application/json");
        }
        requestKind = kind;
        request.SendWebRequest();
    }

    private void Finish()
    {
        long status = request.responseCode;
        bool reached = request.result != UnityWebRequest.Result.ConnectionError;
        string text = request.downloadHandler != null ? request.downloadHandler.text : "";
        int kind = requestKind;
        request.Dispose();
        request = null;
        requestKind = KindNone;

        if (!reached || status >= 500 || status == 0)
        {
            // Unreachable or failing: keep everything and try again shortly.
            nextAttemptAt = Time.time + RetrySeconds;
            if (!zukanLoaded) Controller.SetZukanStatus("サーバーに接続できません（この訪問の記録のみ）");
            if (kind == KindRanking) nextRankingAt = Time.time + RetrySeconds;
            return;
        }

        if (kind == KindCatch)
        {
            // 201: saved. Any other 4xx: the server will never take it, so drop it rather than
            // retrying it forever ahead of the catches behind it.
            Controller.PopPendingCatch();
            if (status == 201)
            {
                zukanWanted = true;
                nextRankingAt = 0f;
            }
        }
        else if (kind == KindZukan)
        {
            if (status == 200 || status == 404)
            {
                // 404: no catches recorded yet.
                Controller.BeginServerZukan();
                if (status == 200) ReadZukan(text);
                Controller.EndServerZukan();
                Controller.SetZukanStatus("保存された記録");
                zukanLoaded = true;
            }
            zukanWanted = false;
        }
        else if (kind == KindRanking)
        {
            if (status == 200) Controller.SetRankingText(FormatRanking(text));
            nextRankingAt = Time.time + RankingRefreshSeconds;
        }
    }

    private void ReadZukan(string json)
    {
        int at = ArrayStart(json, "entries");
        int end;
        while (at >= 0 && (end = NextObject(json, at, out int start)) >= 0)
        {
            string entry = json.Substring(start, end - start + 1);
            string fishName = ReadString(entry, "fishName");
            int count = (int)ReadNumber(entry, "count");
            float min = (float)ReadNumber(entry, "minSize");
            float max = (float)ReadNumber(entry, "maxSize");
            if (fishName != null) Controller.ServerZukanEntry(fishName, count, min, max);
            at = end + 1;
        }
    }

    private string FormatRanking(string json)
    {
        StringBuilder text = new StringBuilder();
        text.Append("<b>レベルランキング</b>\n");
        int at = ArrayStart(json, "entries");
        int end;
        int shown = 0;
        while (at >= 0 && (end = NextObject(json, at, out int start)) >= 0)
        {
            string entry = json.Substring(start, end - start + 1);
            string name = ReadString(entry, "displayName");
            string id = ReadString(entry, "userId");
            int rank = (int)ReadNumber(entry, "rank");
            int level = (int)ReadNumber(entry, "level");
            bool me = id != null && id == userId;
            if (me) text.Append("<color=#D9822B>");
            text.Append(rank);
            text.Append(". Lv.");
            text.Append(level);
            text.Append("  <noparse>");
            text.Append(name != null ? name : "?");
            text.Append("</noparse>");
            if (me) text.Append("</color>");
            text.Append("\n");
            shown++;
            at = end + 1;
        }
        if (shown == 0) text.Append("<color=#7A8A80>まだ誰も記録がないよ</color>");
        return text.ToString();
    }

    // ---- Minimal JSON (the API's flat responses only) ----

    private static void AppendJsonString(StringBuilder json, string value)
    {
        json.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"') json.Append("\\\"");
            else if (c == '\\') json.Append("\\\\");
            else if (c < ' ')
            {
                json.Append("\\u");
                json.Append(((int)c).ToString("x4"));
            }
            else json.Append(c);
        }
        json.Append('"');
    }

    // Decimal with a dot whatever the player's locale, two places.
    private static string FormatDecimal(float value)
    {
        int hundredths = Mathf.RoundToInt(value * 100f);
        string sign = hundredths < 0 ? "-" : "";
        if (hundredths < 0) hundredths = -hundredths;
        int fraction = hundredths % 100;
        return sign + (hundredths / 100) + "." + (fraction < 10 ? "0" : "") + fraction;
    }

    // Index just after the '[' of "key":[ , or -1.
    private static int ArrayStart(string json, string key)
    {
        int at = json.IndexOf("\"" + key + "\"");
        if (at < 0) return -1;
        at = json.IndexOf('[', at);
        return at < 0 ? -1 : at + 1;
    }

    // The next flat {...} object at or after 'from' inside an array; returns the index of its
    // closing brace (or -1 at the end of the array) and its opening one in 'start'.
    private static int NextObject(string json, int from, out int start)
    {
        start = -1;
        bool inString = false;
        for (int i = from; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == ']' && start < 0) return -1;
            else if (c == '{' && start < 0) start = i;
            else if (c == '}' && start >= 0) return i;
        }
        return -1;
    }

    // Index of the value after "key": in a flat object, or -1.
    private static int ValueStart(string obj, string key)
    {
        int at = obj.IndexOf("\"" + key + "\"");
        if (at < 0) return -1;
        at = obj.IndexOf(':', at + key.Length + 2);
        if (at < 0) return -1;
        at++;
        while (at < obj.Length && (obj[at] == ' ' || obj[at] == '\t' || obj[at] == '\n' || obj[at] == '\r')) at++;
        return at < obj.Length ? at : -1;
    }

    private static string ReadString(string obj, string key)
    {
        int at = ValueStart(obj, key);
        if (at < 0 || obj[at] != '"') return null;
        StringBuilder value = new StringBuilder();
        for (int i = at + 1; i < obj.Length; i++)
        {
            char c = obj[i];
            if (c == '"') return value.ToString();
            if (c != '\\' || i + 1 >= obj.Length)
            {
                value.Append(c);
                continue;
            }
            char e = obj[++i];
            if (e == 'n') value.Append('\n');
            else if (e == 't') value.Append('\t');
            else if (e == 'r') value.Append('\r');
            else if (e == 'b') value.Append('\b');
            else if (e == 'f') value.Append('\f');
            else if (e == 'u' && i + 4 < obj.Length)
            {
                value.Append((char)System.Convert.ToInt32(obj.Substring(i + 1, 4), 16));
                i += 4;
            }
            else value.Append(e);
        }
        return null;
    }

    // A JSON number by hand: double.Parse would follow the player's locale.
    private static double ReadNumber(string obj, string key)
    {
        int at = ValueStart(obj, key);
        if (at < 0) return 0;
        double sign = 1;
        if (obj[at] == '-')
        {
            sign = -1;
            at++;
        }
        double value = 0;
        while (at < obj.Length && obj[at] >= '0' && obj[at] <= '9')
        {
            value = value * 10 + (obj[at] - '0');
            at++;
        }
        if (at < obj.Length && obj[at] == '.')
        {
            at++;
            double scale = 0.1;
            while (at < obj.Length && obj[at] >= '0' && obj[at] <= '9')
            {
                value += (obj[at] - '0') * scale;
                scale *= 0.1;
                at++;
            }
        }
        if (at < obj.Length && (obj[at] == 'e' || obj[at] == 'E'))
        {
            at++;
            int expSign = 1;
            if (at < obj.Length && (obj[at] == '+' || obj[at] == '-'))
            {
                if (obj[at] == '-') expSign = -1;
                at++;
            }
            int exponent = 0;
            while (at < obj.Length && obj[at] >= '0' && obj[at] <= '9')
            {
                exponent = exponent * 10 + (obj[at] - '0');
                at++;
            }
            for (int i = 0; i < exponent; i++) value = expSign > 0 ? value * 10 : value / 10;
        }
        return sign * value;
    }
}
