import ctypes as C
import json
from pathlib import Path
from publish import APP, DEPENDENCY, OWNER, ROOT, TITLE, Details, Query, Steam, b8, ptr, u32, u64

EXPECTED_GITHUB = "https://github.com/lingweiliang2510579032/ZEDZONE-AI-Mod"

st = Steam()
st.connect()
try:
    state = json.loads((ROOT / "publish-state.json").read_text(encoding="utf8"))
    item = int(state["itemId"])
    ids = (u64 * 1)(item)
    q = st.query(st.u, ids, 1)
    assert st.longdesc(st.u, q, True) and st.cached(st.u, q, 0) and st.returnchildren(st.u, q, True)
    st.wait(st.send(st.u, q), Query, 3401, 60)
    raw = C.create_string_buffer(65536)
    assert st.getdetails(st.u, q, 0, raw)
    d = Details.from_buffer_copy(raw)
    kids = (u64 * max(1, d.children))()
    if d.children:
        assert st.getchildren(st.u, q, 0, kids, d.children)
    desc = d.description.decode("utf8", "replace")
    expected = (ROOT / "description.bbcode.txt").read_text(encoding="utf8")
    result = {
        "itemId": item,
        "url": f"https://steamcommunity.com/sharedfiles/filedetails/?id={item}",
        "title": d.title.decode("utf8"),
        "visibility": d.visibility,
        "ownerCorrect": d.owner == OWNER,
        "consumerApp": d.consumer,
        "fileSize": d.fileSize,
        "previewSize": d.previewSize,
        "children": list(kids)[: d.children],
        "descriptionExactMatch": desc == expected,
        "githubIncluded": EXPECTED_GITHUB in desc,
        "descriptionBytes": len(desc.encode()),
        "acceptedForUse": bool(d.accepted),
        "banned": bool(d.banned),
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    (ROOT / "verified-public.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf8")
    assert (
        result["title"] == TITLE
        and d.visibility == 0
        and result["ownerCorrect"]
        and d.consumer == APP
        and d.fileSize > 0
        and d.previewSize > 0
        and DEPENDENCY in result["children"]
        and result["descriptionExactMatch"]
        and result["githubIncluded"]
        and not d.banned
    )
    st.release(st.u, q)
finally:
    st.shutdown()
