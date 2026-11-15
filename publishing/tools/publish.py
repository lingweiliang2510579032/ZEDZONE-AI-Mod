"""Resumable Steam UGC publication for AI伴侣（测试版v0.2）.
Requires explicit --execute. No subscribe/unsubscribe or game-file writes.
Uploads bilingual metadata (Simplified Chinese + English), verifies content & preview,
ensures required dependency 3834614258, and confirms public details.
"""
import argparse
import ctypes as C
import hashlib
import json
import os
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DLL = Path(r"D:\steam\steamapps\common\ZED ZONE\ZEDZONE_Data\Plugins\x86_64\steam_api64.dll")
APP = 1211600
OWNER = 76561198272019943
DEPENDENCY = 3834614258
TITLE = "AI伴侣（测试版v0.2）"
TITLE_EN = "AI Companion (Beta v0.2)"
STATE = ROOT / "publish-state.json"

u64 = C.c_uint64
u32 = C.c_uint32
i32 = C.c_int32
b8 = C.c_bool
ptr = C.c_void_p


class Query(C.Structure):
    _pack_ = 8
    _fields_ = [
        ("handle", u64),
        ("result", i32),
        ("returned", u32),
        ("total", u32),
        ("cached", b8),
        ("cursor", C.c_char * 256),
    ]


class Created(C.Structure):
    _pack_ = 8
    _fields_ = [("result", i32), ("id", u64), ("legal", b8)]


class Submitted(C.Structure):
    _pack_ = 8
    _fields_ = [("result", i32), ("legal", b8), ("id", u64)]


class Dependency(C.Structure):
    _pack_ = 8
    _fields_ = [("result", i32), ("parent", u64), ("child", u64)]


class Details(C.Structure):
    _pack_ = 8
    _fields_ = [
        ("id", u64),
        ("result", i32),
        ("filetype", i32),
        ("creator", u32),
        ("consumer", u32),
        ("title", C.c_char * 129),
        ("description", C.c_char * 8000),
        ("owner", u64),
        ("created", u32),
        ("updated", u32),
        ("added", u32),
        ("visibility", i32),
        ("banned", b8),
        ("accepted", b8),
        ("tagsTruncated", b8),
        ("tags", C.c_char * 1025),
        ("file", u64),
        ("preview", u64),
        ("filename", C.c_char * 260),
        ("fileSize", i32),
        ("previewSize", i32),
        ("url", C.c_char * 256),
        ("up", u32),
        ("down", u32),
        ("score", C.c_float),
        ("children", u32),
    ]


class Steam:
    def __init__(self):
        os.environ["SteamAppId"] = str(APP)
        os.environ["SteamGameId"] = str(APP)
        self.dll = C.CDLL(str(DLL))

        def f(name, res, *args):
            fn = getattr(self.dll, name)
            fn.restype = res
            fn.argtypes = args
            return fn

        self.init = f("SteamAPI_Init", b8)
        self.shutdown = f("SteamAPI_Shutdown", None)
        self.run = f("SteamAPI_RunCallbacks", None)
        self.ugc_get = f("SteamAPI_SteamUGC_v016", ptr)
        self.utils_get = f("SteamAPI_SteamUtils_v010", ptr)
        self.user_get = f("SteamAPI_SteamUser_v021", ptr)
        self.identity = f("SteamAPI_ISteamUser_GetSteamID", u64, ptr)
        self.app = f("SteamAPI_ISteamUtils_GetAppID", u32, ptr)
        self.complete = f("SteamAPI_ISteamUtils_IsAPICallCompleted", b8, ptr, u64, C.POINTER(b8))
        self.result = f("SteamAPI_ISteamUtils_GetAPICallResult", b8, ptr, u64, ptr, i32, i32, C.POINTER(b8))
        self.failure = f("SteamAPI_ISteamUtils_GetAPICallFailureReason", i32, ptr, u64)
        self.create = f("SteamAPI_ISteamUGC_CreateItem", u64, ptr, u32, i32)
        self.start = f("SteamAPI_ISteamUGC_StartItemUpdate", u64, ptr, u32, u64)
        self.setuplang = f("SteamAPI_ISteamUGC_SetItemUpdateLanguage", b8, ptr, u64, C.c_char_p)
        self.settitle = f("SteamAPI_ISteamUGC_SetItemTitle", b8, ptr, u64, C.c_char_p)
        self.setdesc = f("SteamAPI_ISteamUGC_SetItemDescription", b8, ptr, u64, C.c_char_p)
        self.setcontent = f("SteamAPI_ISteamUGC_SetItemContent", b8, ptr, u64, C.c_char_p)
        self.setpreview = f("SteamAPI_ISteamUGC_SetItemPreview", b8, ptr, u64, C.c_char_p)
        self.setvisibility = f("SteamAPI_ISteamUGC_SetItemVisibility", b8, ptr, u64, i32)
        self.submit = f("SteamAPI_ISteamUGC_SubmitItemUpdate", u64, ptr, u64, C.c_char_p)
        self.adddep = f("SteamAPI_ISteamUGC_AddDependency", u64, ptr, u64, u64)
        self.query = f("SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest", u64, ptr, C.POINTER(u64), u32)
        self.returnchildren = f("SteamAPI_ISteamUGC_SetReturnChildren", b8, ptr, u64, b8)
        self.longdesc = f("SteamAPI_ISteamUGC_SetReturnLongDescription", b8, ptr, u64, b8)
        self.setlang = f("SteamAPI_ISteamUGC_SetLanguage", b8, ptr, u64, C.c_char_p)
        self.cached = f("SteamAPI_ISteamUGC_SetAllowCachedResponse", b8, ptr, u64, u32)
        self.send = f("SteamAPI_ISteamUGC_SendQueryUGCRequest", u64, ptr, u64)
        self.getdetails = f("SteamAPI_ISteamUGC_GetQueryUGCResult", b8, ptr, u64, u32, ptr)
        self.getchildren = f("SteamAPI_ISteamUGC_GetQueryUGCChildren", b8, ptr, u64, u32, C.POINTER(u64), u32)
        self.release = f("SteamAPI_ISteamUGC_ReleaseQueryUGCRequest", b8, ptr, u64)
        self.progress = f("SteamAPI_ISteamUGC_GetItemUpdateProgress", i32, ptr, u64, C.POINTER(u64), C.POINTER(u64))

    def connect(self):
        if not self.init():
            raise RuntimeError("SteamAPI_Init false; client/account/app not ready")
        self.u = self.ugc_get()
        self.utils = self.utils_get()
        self.user = self.user_get()
        if self.app(self.utils) != APP or self.identity(self.user) != OWNER:
            raise RuntimeError("App or owner differs from explicitly intended publisher")

    def wait(self, call, typ, callback, timeout=180, update=None):
        if not call:
            raise RuntimeError("Invalid async handle")
        until = time.monotonic() + timeout
        last = 0
        while time.monotonic() < until:
            self.run()
            failed = b8()
            if self.complete(self.utils, call, C.byref(failed)):
                if failed.value:
                    raise RuntimeError(f"Async I/O failure {self.failure(self.utils, call)}")
                r = typ()
                failed = b8()
                if not self.result(self.utils, call, C.byref(r), C.sizeof(r), callback, C.byref(failed)) or failed.value:
                    raise RuntimeError("GetAPICallResult failed; check callback ID/layout")
                if r.result != 1:
                    raise RuntimeError(f"Steam result {r.result}")
                return r
            if update and time.monotonic() - last > 3:
                done = u64()
                total = u64()
                status = self.progress(self.u, update, C.byref(done), C.byref(total))
                print("upload", status, done.value, total.value, flush=True)
                last = time.monotonic()
            time.sleep(0.1)
        raise TimeoutError(f"Steam async {call} pending; DO NOT recreate item; state preserved")

    def details(self, item, lang=b"schinese"):
        ids = (u64 * 1)(item)
        q = self.query(self.u, ids, 1)
        if not q or q == 2**64 - 1:
            raise RuntimeError("Invalid query")
        try:
            if not self.returnchildren(self.u, q, True):
                raise RuntimeError("SetReturnChildren rejected")
            self.longdesc(self.u, q, True)
            if not self.setlang(self.u, q, lang):
                raise RuntimeError("SetLanguage rejected")
            self.cached(self.u, q, 0)
            r = self.wait(self.send(self.u, q), Query, 3401, 60)
            if not r.returned:
                raise RuntimeError("Item not returned")
            raw = C.create_string_buffer(65536)
            if not self.getdetails(self.u, q, 0, raw):
                raise RuntimeError("GetQueryUGCResult false")
            d = Details.from_buffer_copy(raw)
            if d.result != 1 or d.id != item:
                raise RuntimeError("Wrong item details")
            kids = (u64 * max(1, d.children))()
            if d.children and not self.getchildren(self.u, q, 0, kids, d.children):
                raise RuntimeError("Get children failed")
            return {
                "id": d.id,
                "title": d.title.decode("utf8", "replace"),
                "owner": d.owner,
                "creatorApp": d.creator,
                "consumerApp": d.consumer,
                "visibility": d.visibility,
                "fileSize": d.fileSize,
                "previewSize": d.previewSize,
                "children": list(kids)[: d.children],
            }
        finally:
            self.release(self.u, q)


def save(s):
    tmp = STATE.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(s, ensure_ascii=False, indent=2), encoding="utf8")
    os.replace(tmp, STATE)


def fingerprint():
    files = []
    for p in sorted((ROOT / "content").rglob("*")):
        if p.is_file():
            files.append((str(p.relative_to(ROOT)), hashlib.sha256(p.read_bytes()).hexdigest()))
    for p in [ROOT / "preview.png", ROOT / "description.bbcode.txt", ROOT / "description_en.bbcode.txt"]:
        files.append((p.name, hashlib.sha256(p.read_bytes()).hexdigest()))
    return hashlib.sha256(json.dumps(files, ensure_ascii=False).encode()).hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--execute", action="store_true")
    ap.add_argument("--note", default="测试版 v0.2：新增游戏本体 10 语言支持，优化使用说明与设置界面")
    a = ap.parse_args()
    if not a.execute:
        raise SystemExit("No mutations performed. Pass --execute explicitly after reviewing package.")
    assert len(TITLE.encode()) < 129
    assert len(TITLE_EN.encode()) < 129
    content = (ROOT / "content").resolve()
    preview = (ROOT / "preview.png").resolve()
    desc = (ROOT / "description.bbcode.txt").read_text(encoding="utf8")
    desc_en = (ROOT / "description_en.bbcode.txt").read_text(encoding="utf8")
    assert len(desc.encode("utf8")) < 7950, f"Description too long: {len(desc.encode('utf8'))}"
    assert len(desc_en.encode("utf8")) < 7950, f"English description too long: {len(desc_en.encode('utf8'))}"
    if not content.is_dir() or not preview.is_file():
        raise RuntimeError("Content/preview missing")
    if preview.stat().st_size >= 1024 * 1024:
        raise RuntimeError("Preview must be <1 MiB")
    s = json.loads(STATE.read_text(encoding="utf8")) if STATE.exists() else {"appId": APP, "owner": str(OWNER), "dependency": str(DEPENDENCY)}
    if s.get("appId") != APP or s.get("owner") != str(OWNER) or s.get("dependency") != str(DEPENDENCY):
        raise RuntimeError("State identity mismatch")
    digest = fingerprint()
    st = Steam()
    initialized = False
    try:
        st.connect()
        initialized = True
        if s.get("pendingCreate") and not s.get("itemId"):
            raise RuntimeError("Create was submitted but result not captured. Recover manually; do not create duplicate.")
        if not s.get("itemId"):
            s["pendingCreate"] = True
            save(s)
            call = st.create(st.u, APP, 0)
            s["createCall"] = str(call)
            save(s)
            r = st.wait(call, Created, 3403, 90)
            s["itemId"] = str(r.id)
            s["pendingCreate"] = False
            s["legalRequired"] = bool(r.legal)
            save(s)
            print("CREATED item", r.id, flush=True)
            if r.legal:
                raise RuntimeError("Workshop legal agreement acceptance required; STOP")
        item = int(s["itemId"])
        d = st.details(item, b"schinese")
        s["details"] = d
        save(s)
        if d["owner"] != OWNER or d["consumerApp"] != APP:
            raise RuntimeError("Existing item is not owned by intended publisher/app")
        if s.get("legalRequired"):
            raise RuntimeError("Workshop legal agreement pending")
        if not s.get("uploaded") or s.get("packageHash") != digest:
            h = st.start(st.u, APP, item)
            if not h or h == 2**64 - 1:
                raise RuntimeError("StartItemUpdate invalid")
            vis = 0 if s.get("publicVerified") else 2
            for name, fn, value in [
                ("updateLanguage", st.setuplang, b"schinese"),
                ("visibility", st.setvisibility, vis),
                ("title", st.settitle, TITLE.encode("utf8")),
                ("description", st.setdesc, desc.encode("utf8")),
                ("content", st.setcontent, str(content).encode("utf8")),
                ("preview", st.setpreview, str(preview).encode("utf8")),
            ]:
                if not fn(st.u, h, value):
                    raise RuntimeError("Set " + name + " rejected")
            call = st.submit(st.u, h, a.note.encode("utf8"))
            s["uploadCall"] = str(call)
            save(s)
            r = st.wait(call, Submitted, 3404, 300, h)
            if r.id != item:
                raise RuntimeError("Wrong upload item")
            if r.legal:
                s["legalRequired"] = True
                save(s)
                raise RuntimeError("Workshop legal agreement required after submit")

            h2 = st.start(st.u, APP, item)
            if not h2 or h2 == 2**64 - 1:
                raise RuntimeError("StartItemUpdate (english) invalid")
            for name, fn, value in [
                ("updateLanguage(en)", st.setuplang, b"english"),
                ("title(en)", st.settitle, TITLE_EN.encode("utf8")),
                ("description(en)", st.setdesc, desc_en.encode("utf8")),
            ]:
                if not fn(st.u, h2, value):
                    raise RuntimeError("Set " + name + " rejected")
            call2 = st.submit(st.u, h2, b"v0.2: Add 10-language support and updated usage guide")
            s["uploadEnCall"] = str(call2)
            save(s)
            r2 = st.wait(call2, Submitted, 3404, 180, h2)
            if r2.id != item:
                raise RuntimeError("Wrong english upload item")
            s["uploaded"] = True
            s["packageHash"] = digest
            s["legalRequired"] = bool(r2.legal)
            save(s)
            if r2.legal:
                raise RuntimeError("Workshop legal agreement required after english submit")
        d = st.details(item, b"schinese")
        d_en = st.details(item, b"english")
        if d["fileSize"] <= 0 or d["previewSize"] <= 0 or d["title"] != TITLE or d_en["title"] != TITLE_EN or d["visibility"] not in (0, 2):
            raise RuntimeError(f"Upload or preview verification failed: zh={d}, en={d_en}")
        if DEPENDENCY not in d["children"]:
            call = st.adddep(st.u, item, DEPENDENCY)
            s["dependencyCall"] = str(call)
            save(s)
            r = st.wait(call, Dependency, 3412, 90)
            if r.parent != item or r.child != DEPENDENCY:
                raise RuntimeError("Wrong dependency result")
        d = st.details(item, b"schinese")
        if DEPENDENCY not in d["children"]:
            raise RuntimeError("Required child not verified; item stays private")
        s["dependencyVerified"] = True
        s["details"] = d
        s["detailsEn"] = d_en
        save(s)
        if d["visibility"] != 0:
            h = st.start(st.u, APP, item)
            if not st.setvisibility(st.u, h, 0):
                raise RuntimeError("Set public rejected")
            call = st.submit(st.u, h, b"Publish AI companion v0.2 with required code mod loader dependency")
            s["publicCall"] = str(call)
            save(s)
            r = st.wait(call, Submitted, 3404, 180, h)
            if r.legal:
                s["legalRequired"] = True
                save(s)
                raise RuntimeError("Legal required on public submit")
        d = st.details(item, b"schinese")
        d_en = st.details(item, b"english")
        if d["visibility"] != 0 or DEPENDENCY not in d["children"] or d["owner"] != OWNER or d["title"] != TITLE or d_en["title"] != TITLE_EN:
            raise RuntimeError(f"Final public verification failed: zh={d}, en={d_en}")
        s["publicVerified"] = True
        s["details"] = d
        s["detailsEn"] = d_en
        s["url"] = f"https://steamcommunity.com/sharedfiles/filedetails/?id={item}"
        save(s)
        print(json.dumps(s, ensure_ascii=False, indent=2))
    except Exception as e:
        s["lastError"] = str(e)
        save(s)
        raise
    finally:
        if initialized:
            st.shutdown()


if __name__ == "__main__":
    main()
