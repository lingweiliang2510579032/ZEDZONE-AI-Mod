import ctypes as C
import json
from publish import APP, DEPENDENCY, OWNER, ROOT, TITLE, TITLE_EN, Details, Query, Steam

EXPECTED_GITHUB = "https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod"


def query_lang(st: Steam, item: int, lang: bytes):
    ids = (C.c_uint64 * 1)(item)
    q = st.query(st.u, ids, 1)
    assert st.longdesc(st.u, q, True) and st.cached(st.u, q, 0) and st.returnchildren(st.u, q, True)
    assert st.setlang(st.u, q, lang)
    st.wait(st.send(st.u, q), Query, 3401, 60)
    raw = C.create_string_buffer(65536)
    assert st.getdetails(st.u, q, 0, raw)
    d = Details.from_buffer_copy(raw)
    kids = (C.c_uint64 * max(1, d.children))()
    if d.children:
        assert st.getchildren(st.u, q, 0, kids, d.children)
    children = list(kids)[: d.children]
    st.release(st.u, q)
    return d, children


st = Steam()
st.connect()
try:
    state = json.loads((ROOT / "publish-state.json").read_text(encoding="utf8"))
    item = int(state["itemId"])
    d_zh, kids_zh = query_lang(st, item, b"schinese")
    d_en, kids_en = query_lang(st, item, b"english")
    desc_zh = d_zh.description.decode("utf8", "replace")
    desc_en = d_en.description.decode("utf8", "replace")
    expected_zh = (ROOT / "description.bbcode.txt").read_text(encoding="utf8")
    expected_en = (ROOT / "description_en.bbcode.txt").read_text(encoding="utf8")
    result = {
        "itemId": item,
        "url": f"https://steamcommunity.com/sharedfiles/filedetails/?id={item}",
        "title": d_zh.title.decode("utf8"),
        "titleEn": d_en.title.decode("utf8"),
        "visibility": d_zh.visibility,
        "ownerCorrect": d_zh.owner == OWNER,
        "consumerApp": d_zh.consumer,
        "fileSize": d_zh.fileSize,
        "previewSize": d_zh.previewSize,
        "children": kids_zh,
        "descriptionExactMatch": desc_zh == expected_zh,
        "descriptionEnExactMatch": desc_en == expected_en,
        "githubIncluded": EXPECTED_GITHUB in desc_zh and EXPECTED_GITHUB in desc_en,
        "descriptionBytes": len(desc_zh.encode()),
        "descriptionEnBytes": len(desc_en.encode()),
        "acceptedForUse": bool(d_zh.accepted),
        "banned": bool(d_zh.banned),
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    (ROOT / "verified-public.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf8")
    assert (
        result["title"] == TITLE
        and result["titleEn"] == TITLE_EN
        and d_zh.visibility == 0
        and result["ownerCorrect"]
        and d_zh.consumer == APP
        and d_zh.fileSize > 0
        and d_zh.previewSize > 0
        and DEPENDENCY in kids_zh
        and DEPENDENCY in kids_en
        and result["descriptionExactMatch"]
        and result["descriptionEnExactMatch"]
        and result["githubIncluded"]
        and not d_zh.banned
    )
finally:
    st.shutdown()
