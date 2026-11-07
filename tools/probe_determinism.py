#!/usr/bin/env python3
"""Which kind of nondeterminism does a chat completions endpoint have? Black-box tests, one detector per cause.

Follows "Defeating Nondeterminism in LLM Inference" (Thinking Machines, 2025): kernels repeat run to run, but their
results depend on batch size, and server load sets batch size. Its other causes are KV cache layout and chunked prefill.
Three more causes are outside the article and matter here: sampler state shared between requests, a vision tower, and
runtime decisions that depend on measured speed.

How the causes are kept apart:
  - Temperature 0 removes sampling, so a greedy difference comes from the numbers. A seeded difference where greedy holds
    comes from sampling.
  - Every detector repeats its own unperturbed request first. If that control doesn't repeat, the verdict is NOISY and
    the perturbation takes no blame.
  - Each repeated request runs once first and that reply is thrown away, so a prompt's first cache miss only counts in
    the detectors about caching and loading.
  - Overlap is proven from stream timing. A queued request reports NOT_REACHED, never HOLDS.
  - Where logprobs exist, they catch numbers that moved before the text did (DRIFT).

Verdicts:
  HOLDS        perturbation applied, output unchanged. No evidence of this cause, which is not proof of its absence.
  BREAKS       control repeated, perturbation changed the output. "repeatable" = same wrong answer every time,
               "racy" = the answer varies between rounds, so timing is involved.
  DRIFT        text identical, logprobs moved.
  NOISY        the control didn't repeat, so this cause can't be isolated.
  NOT_REACHED  the situation never happened (queued, no cache hit).
  ERROR, SKIP  request failed; engine lacks what the detector needs.

Limits: request arrival order only stands in for batch position, since no engine reports its slots. Prefill chunk size
can't be set from outside, so chunking is only exercised through prefix reuse. Few rounds can miss rare near-ties (the
article needed 1000 completions to count them), so raise --rounds for a verdict that matters. The endpoint must have no
other clients while this runs: their requests would change the batches, and nothing here can see them. The chat
completions API has no way to unload a model, so a difference on the first request after a load can't be tested.

Usage: probe_determinism.py URL API_KEY MODEL [--quick] [--only batch,vision] [--rounds N]
       probe_determinism.py --list
URL is the endpoint or its base, such as http://localhost:8080/v1. Use "none" for API_KEY when there isn't one.
"""
import argparse, base64, json, os, struct, threading, time, urllib.error, urllib.request, zlib

PROMPTS = ["Tell me about Richard Feynman.",
           "Explain in detail how a suspension bridge carries its load, and what limits its span.",
           "Write a short poem about walruses."]
COMPANIONS = ["Write a long, detailed history of the printing press, century by century.",
              "Explain at length how a four-stroke diesel engine works, one part at a time.",
              "Describe every stage of making paper by hand, with as much detail as you can."]
BUSY_COVER = 0.5  # how much of the target's stream a companion must span for "they ran as a batch" to count
BUSY_SWITCHES = 3  # the two streams must take turns this often, which a queue or a buffered dump can't do
SEVERITY = ["BREAKS", "DRIFT", "NOISY", "ERROR", "NOT_REACHED", "HOLDS", "SKIP"]
DETECTORS = {}


def detector(name, group, cause, needs=""):
    def register(fn):
        DETECTORS[name] = {"fn": fn, "group": group, "cause": cause, "needs": needs, "arg": None}
        return fn
    return register


def say(*a):
    print(*a, flush=True)


def filler(tag, lines):
    # A long prompt that is the same on every run: prefill and prefix-cache tests need real length without randomness.
    places = ["Elmford", "Carrow", "Dunmere", "Ashby", "Penhale", "Rook Hill", "Tarn", "Wexley"]
    return "\n".join(f"{tag} record {i}: the station at {places[i % 8]} logged {(i * 37) % 91 + 3} mm of rain "
                     f"and a wind of {(i * 53) % 67 + 5} km/h from bearing {(i * 29) % 360}." for i in range(lines))


def endpoint(url):
    url = url.rstrip("/")
    if url.endswith("/chat/completions"):
        return url
    return url + "/chat/completions" if url.endswith("/v1") else url + "/v1/chat/completions"


def png(seed, size=512, solid=None):
    # Colored blocks from a fixed generator: enough structure to work the vision tower, and identical on every run.
    state, cells = seed, 16
    colors = []
    for _ in range(cells * cells):
        state = (state * 1103515245 + 12345) & 0x7fffffff
        colors.append(solid or bytes([(state >> 16) & 255, (state >> 8) & 255, state & 255]))
    block = size // cells
    rows = b"".join(b"\x00" + b"".join(colors[y * cells + x] * block for x in range(cells))
                    for y in range(cells) for _ in range(block))
    chunk = lambda t, d: struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d))
    data = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", block * cells, block * cells, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))
    return "data:image/png;base64," + base64.b64encode(data).decode()


def first_diff(x, y):
    for i in range(min(len(x), len(y))):
        if x[i] != y[i]:
            return i
    return -1 if len(x) == len(y) else min(len(x), len(y))


def number_diff(a, b):
    # Largest logprob change over the tokens both replies share; (0, -1) when bitwise equal.
    biggest, first = 0.0, -1
    for i, (x, y) in enumerate(zip(a["lps"], b["lps"])):
        if x[0] != y[0]:
            break
        d = abs(x[1] - y[1])
        if d and first < 0:
            first = i
        biggest = max(biggest, d)
    return biggest, first


def overlap(t, c):
    # cover: how much of the target's stream the companion spanned. switches: how often the two streams took turns.
    if not t["times"] or not c["times"]:
        return {"cover": 0.0, "switches": 0}
    span = t["times"][-1] - t["times"][0]
    both = min(t["times"][-1], c["times"][-1]) - max(t["times"][0], c["times"][0])
    merged = sorted([(x, 0) for x in t["times"]] + [(x, 1) for x in c["times"]])
    switches = sum(1 for p, q in zip(merged, merged[1:]) if p[1] != q[1])
    return {"cover": round(max(0.0, both) / span, 3) if span > 0 else 0.0, "switches": switches}


class Probe:
    def __init__(self, cfg):
        self.cfg, self.model, self.url = cfg, cfg.model, endpoint(cfg.url)
        self.headers = {"Content-Type": "application/json"}
        if cfg.api_key.lower() != "none":
            self.headers["Authorization"] = "Bearer " + cfg.api_key
        self.usage_option = True
        self.caps = {}

    # --- endpoint ------------------------------------------------------------------------------------------------
    def send(self, spec, on_chunk=None):
        body = dict(spec, model=self.model, stream=True)
        if self.usage_option:
            body["stream_options"] = {"include_usage": True}
        lp = body.pop("numbers", False)
        if lp:
            body.update(logprobs=True, top_logprobs=1)
        req = urllib.request.Request(self.url, json.dumps(body).encode(), self.headers)
        r = {"text": "", "times": [], "lps": [], "error": None, "finish": None, "cached": None}
        try:
            with urllib.request.urlopen(req, timeout=1500) as resp:
                for raw in resp:
                    line = raw.decode("utf-8", "replace").strip()
                    if not line.startswith("data:") or line.endswith("[DONE]"):
                        continue
                    try:
                        msg = json.loads(line[5:])
                    except ValueError:
                        continue
                    details = (msg.get("usage") or {}).get("prompt_tokens_details") or {}
                    if details.get("cached_tokens") is not None:
                        r["cached"] = details["cached_tokens"]
                    for c in msg.get("choices") or []:
                        d = c.get("delta") or {}
                        t = (d.get("reasoning_content") or d.get("reasoning") or "") + (d.get("content") or "")
                        r["finish"] = c.get("finish_reason") or r["finish"]
                        for e in (c.get("logprobs") or {}).get("content") or []:
                            r["lps"].append((e.get("token"), e.get("logprob")))
                        if t:
                            r["text"] += t
                            r["times"].append(time.time())
                            if on_chunk:
                                on_chunk(len(r["times"]))
        except Exception as e:
            detail = e.read()[:200].decode("utf-8", "replace") if isinstance(e, urllib.error.HTTPError) else ""
            r["error"] = f"{e} {detail}".strip()
        if not r["error"] and not r["text"]:
            r["error"] = "empty reply"
        return r

    def together(self, target, comps, join_at=None, stagger=0.0):
        # Runs target and companions at once. join_at holds the companions until the target has streamed that many chunks.
        go = threading.Event()
        if join_at is None:
            go.set()
        def chunked(n):
            if join_at is not None and n >= join_at:
                go.set()
        out = [None] * len(comps)
        def companion(i, spec):
            go.wait()
            if stagger:
                time.sleep(stagger)
            out[i] = self.send(spec)
        threads = [threading.Thread(target=companion, args=(i, s)) for i, s in enumerate(comps)]
        for t in threads:
            t.start()
        reply = self.send(target, chunked)
        go.set()  # the target may end before the trigger; companions must still run so the threads finish
        for t in threads:
            t.join()
        return reply, out

    # --- requests ------------------------------------------------------------------------------------------------
    def spec(self, prompt, seed=None, numbers=False, tokens=None):
        # seed=None is greedy. It still sends a seed so both modes take the same request path through the engine.
        return {"messages": [{"role": "user", "content": prompt}], "max_tokens": tokens or self.cfg.max_tokens,
                "temperature": 0.0 if seed is None else 1.0, "seed": 7 if seed is None else seed, "numbers": numbers}

    def image(self, seed=None, picture=1, numbers=False):
        content = [{"type": "text", "text": "Describe this image in detail, block by block."},
                   {"type": "image_url", "image_url": {"url": png(picture)}}]
        return dict(self.spec("", seed, numbers), messages=[{"role": "user", "content": content}])

    def companions(self, width, seed=None, tokens=None):
        # They must outlast the target, or the batch shrinks partway through and the test measures something else.
        tokens = tokens or self.cfg.max_tokens * 3
        return [self.spec(COMPANIONS[i % 3], None if seed is None else 1000 + i, tokens=tokens) for i in range(width - 1)]

    def targets(self, seed=None, numbers=False):
        return [self.spec(p, seed, numbers) for p in PROMPTS[:self.cfg.prompts]]

    # --- judging -------------------------------------------------------------------------------------------------
    def finding(self, verdict, notes=None, **evidence):
        return {"verdict": verdict, "notes": notes or [], **evidence}

    def vary(self, base, r):
        # None when r matches base. Text is compared first; logprobs only matter when the text agreed.
        if r["text"] != base["text"]:
            return f"text@{first_diff(r['text'], base['text'])}/{len(base['text'])}"
        if base["lps"] and r["lps"]:
            d, at = number_diff(base, r)
            if d:
                return f"logprob {d:.2e}@tok{at}"
        return None

    def isolate(self, specs, perturb, rounds=None):
        """The shared shape: repeat the plain request, perturb it, then repeat it once more.

        perturb(spec, base) returns (reply, reached, info). A round where the situation didn't happen doesn't count."""
        rounds = rounds or self.cfg.rounds
        results = []
        for spec in specs:
            self.send(spec)
            base = self.send(spec)
            second = self.send(spec)
            if base["error"] or second["error"]:
                results.append(self.finding("ERROR", [base["error"] or second["error"]]))
                continue
            control = self.vary(base, second)
            if control:
                results.append(self.finding("NOISY", [f"control differs: {control}"]))
                continue
            seen, reached_infos, skipped = [], [], 0
            for _ in range(rounds):
                reply, reached, info = perturb(spec, base)
                if reply is None or reply["error"]:
                    results.append(self.finding("ERROR", [reply["error"] if reply else info]))
                    seen = None
                    break
                if not reached:
                    skipped += 1
                    reached_infos.append(info)
                    continue
                seen.append(reply)
                reached_infos.append(info)
            if seen is None:
                continue
            if not seen:
                results.append(self.finding("NOT_REACHED", [f"{skipped} of {rounds} rounds missed it"], info=reached_infos))
                continue
            changes = [self.vary(base, r) for r in seen]
            after = self.send(spec)
            leaked = not after["error"] and self.vary(base, after)
            notes = []
            if skipped:
                notes.append(f"{skipped} of {rounds} rounds not reached")
            if leaked:
                notes.append(f"stayed changed afterward ({leaked})")
            hits = [c for c in changes if c]
            if not hits and not leaked:
                results.append(self.finding("HOLDS", notes, info=reached_infos))
            elif hits and all(not c.startswith("text") for c in hits):
                results.append(self.finding("DRIFT", notes + hits, info=reached_infos))
            else:
                kinds = len({r["text"] for r in seen})
                shape = "repeatable" if kinds == 1 and len(hits) == len(seen) else f"racy, {kinds} outputs in {len(seen)}"
                results.append(self.finding("BREAKS", notes + [shape] + hits, info=reached_infos))
        return self.merge(results)

    def merge(self, results):
        verdict = min((r["verdict"] for r in results), key=SEVERITY.index)
        notes = []
        for r in results:
            notes += [n for n in r["notes"] if n not in notes]
        return self.finding(verdict, notes, info=[i for r in results for i in r.get("info", [])])

    def repeats(self, specs):
        # With no perturbation there is no control to separate out: the repeat is the test.
        results = []
        for spec in specs:
            self.send(spec)
            replies = [self.send(spec) for _ in range(self.cfg.rounds + 2)]
            errors = [r["error"] for r in replies if r["error"]]
            if errors:
                results.append(self.finding("ERROR", errors[:1]))
                continue
            counts = {}
            for r in replies:
                counts[r["text"]] = counts.get(r["text"], 0) + 1
            hits = [c for c in (self.vary(replies[0], r) for r in replies[1:]) if c]
            if len(counts) > 1:
                note = f"{len(counts)} outputs in {len(replies)}, most common {max(counts.values())}"
                results.append(self.finding("BREAKS", [note] + hits))
            else:
                results.append(self.finding("DRIFT" if hits else "HOLDS", hits))
        return self.merge(results)

    def batched(self, specs, width, seed=None):
        def perturb(spec, base):
            reply, comps = self.together(spec, self.companions(width, seed))
            bad = next((c["error"] for c in comps if c["error"]), None)
            if bad:
                return None, False, bad
            stats = [overlap(reply, c) for c in comps]
            return reply, all(s["cover"] >= BUSY_COVER and s["switches"] >= BUSY_SWITCHES for s in stats), stats
        return self.isolate(specs, perturb)


# --- run to run: the article's baseline, one request at a time -----------------------------------------------------
@detector("repeat_greedy", "series", "numbers vary run to run with no other request present")
def repeat_greedy(p):
    return p.repeats(p.targets())


@detector("repeat_seeded", "series", "the sampler doesn't replay from its seed")
def repeat_seeded(p):
    return p.repeats(p.targets(seed=7))


@detector("seed_interleave", "series", "sampler state survives from the previous request", "seed")
def seed_interleave(p):
    def perturb(spec, base):
        p.send(dict(spec, seed=8))
        return p.send(spec), True, "after another seed"
    return p.isolate(p.targets(seed=7), perturb)


# --- prior traffic and cache state ---------------------------------------------------------------------------------
@detector("prefix_cache", "state", "KV cache layout: reusing a prefix changes the numbers (article: chunked prefill)")
def prefix_cache(p):
    doc = filler("Weather", p.cfg.long_lines)
    y = p.spec(doc + "\n\nSummarize the rainfall pattern and name the wettest station.")
    x = p.spec(doc + "\n\nList the windiest stations and their bearings.", tokens=40)
    flush = p.spec(filler("Tide", p.cfg.long_lines) + "\n\nSummarize this.", tokens=40)
    def miss():
        p.send(flush)
        return p.send(y)
    base, second = miss(), miss()
    if base["error"] or second["error"]:
        return p.finding("ERROR", [base["error"] or second["error"]])
    control = p.vary(base, second)
    if control:
        return p.finding("NOISY", [f"cold-prefix control differs: {control}"])
    p.send(flush)
    p.send(x)
    partial = p.send(y)
    full = p.send(y)
    if partial["error"] or full["error"]:
        return p.finding("ERROR", [partial["error"] or full["error"]])
    cached = {"miss": second["cached"], "partial": partial["cached"], "full": full["cached"]}
    notes = [f"cached tokens {cached}"]
    if cached["miss"]:
        # A hit compared with a hit says nothing about reuse. Only a real miss can be the baseline.
        return p.finding("NOT_REACHED", notes + ["the flush didn't evict, so nothing was compared with a cache miss"], cached=cached)
    if cached["partial"] == 0 and cached["full"] == 0:
        return p.finding("NOT_REACHED", notes + ["engine reports no cache hit"], cached=cached)
    changes = {k: p.vary(base, r) for k, r in (("partial", partial), ("full", full)) if r["cached"] != 0}
    if not changes:
        return p.finding("NOT_REACHED", notes + ["engine reports no cache hit"], cached=cached)
    hits = [f"{k}: {v}" for k, v in changes.items() if v]
    if not hits:
        return p.finding("HOLDS", notes, cached=cached)
    numeric = all("logprob" in h for h in hits)
    return p.finding("DRIFT" if numeric else "BREAKS", notes + hits, cached=cached)


@detector("idle_gap", "state", "behavior depends on elapsed time (idle work, power state, timing-driven decisions)")
def idle_gap(p):
    def perturb(spec, base):
        time.sleep(p.cfg.idle)
        return p.send(spec), True, f"after {p.cfg.idle}s idle"
    return p.isolate(p.targets()[:1], perturb, rounds=2)


@detector("after_traffic", "state", "earlier unrelated requests leave state behind (adaptive speculation, allocators)")
def after_traffic(p):
    def perturb(spec, base):
        for i in range(p.cfg.traffic):
            p.send(p.spec(COMPANIONS[i % 3], tokens=60))
        return p.send(spec), True, f"after {p.cfg.traffic} other requests"
    return p.isolate(p.targets()[:1], perturb, rounds=2)


# --- batch invariance: the article's main cause, and whether it is the kernels or the sampler --------------------
def batch_detector(width):
    def greedy(p):
        return p.batched(p.targets(), width)
    def seeded(p):
        return p.batched(p.targets(seed=7), width, seed=1000)
    DETECTORS[f"batch_greedy_w{width}"] = {
        "fn": greedy, "group": "batch", "needs": "",
        "cause": f"kernels depend on batch size: greedy output changes with {width} running (no sampling involved)"}
    DETECTORS[f"batch_seeded_w{width}"] = {
        "fn": seeded, "group": "batch", "needs": "seed",
        "cause": f"seeded output changes with {width} running; if greedy held, sampling state is shared between requests"}


@detector("join_mid", "batch", "the batch growing during decode disturbs a running request")
def join_mid(p):
    def perturb(spec, base):
        reply, comps = p.together(spec, p.companions(2), join_at=max(4, len(base["times"]) // 4))
        if comps[0]["error"]:
            return None, False, comps[0]["error"]
        stats = overlap(reply, comps[0])
        return reply, stats["switches"] >= BUSY_SWITCHES, stats
    return p.isolate(p.targets(), perturb)


@detector("leave_mid", "batch", "the batch shrinking during decode disturbs a running request")
def leave_mid(p):
    def perturb(spec, base):
        short = p.companions(2, tokens=max(8, len(base["times"]) // 4))
        reply, comps = p.together(spec, short)
        c = comps[0]
        if c["error"]:
            return None, False, c["error"]
        stats = overlap(reply, c)
        # The companion has to finish while the target is still streaming, or the batch never shrank.
        left = bool(c["times"] and reply["times"]) and c["times"][-1] < reply["times"][-1]
        return reply, left and stats["switches"] >= BUSY_SWITCHES, stats
    return p.isolate(p.targets(), perturb)


@detector("prefill_mix", "batch", "another request's prompt prefill sharing the batch disturbs decode (article: chunked prefill)")
def prefill_mix(p):
    doc = filler("Harbor", p.cfg.long_lines) + "\n\nSummarize this."
    def perturb(spec, base):
        reply, comps = p.together(spec, [p.spec(doc, tokens=40)], join_at=max(2, len(base["times"]) // 10))
        c = comps[0]
        if c["error"]:
            return None, False, c["error"]
        # Its first token arriving before the target finished means the prefill ran beside it. A queued one arrives after.
        beside = bool(c["times"] and reply["times"]) and c["times"][0] < reply["times"][-1]
        return reply, beside, overlap(reply, c)
    return p.isolate([p.spec(PROMPTS[0], tokens=p.cfg.max_tokens * 2)], perturb)


@detector("position", "batch", "arrival order within the batch matters (article: batch-position invariance)")
def position(p):
    # Needs no solo baseline, so an engine with batch-size dependence can still pass or fail this on its own.
    x, y = p.spec(PROMPTS[0]), p.spec(PROMPTS[1])
    def pair(first, second):
        a, comps = p.together(first, [second], stagger=0.1)
        return a, comps[0]
    forward = [pair(x, y) for _ in range(2)]
    backward = [pair(y, x) for _ in range(2)]
    pairs = forward + backward
    if any(a["error"] or b["error"] for a, b in pairs):
        return p.finding("ERROR", [next(r["error"] for pr in pairs for r in pr if r["error"])])
    if not all(overlap(a, b)["switches"] >= BUSY_SWITCHES for a, b in pairs):
        return p.finding("NOT_REACHED", ["requests didn't take turns"])
    xs = [forward[0][0], forward[1][0], backward[0][1], backward[1][1]]
    ys = [forward[0][1], forward[1][1], backward[0][0], backward[1][0]]
    notes = []
    for label, r in (("first prompt", xs), ("second prompt", ys)):
        if p.vary(r[0], r[1]) or p.vary(r[2], r[3]):
            notes.append(f"{label}: same order doesn't repeat, so timing is involved")
    if notes:
        return p.finding("NOISY", notes)
    hits = [v for v in (p.vary(xs[0], xs[2]), p.vary(ys[0], ys[2])) if v]
    if not hits:
        return p.finding("HOLDS")
    return p.finding("BREAKS", hits)


# --- logprobs: the same questions, sensitive to numbers that moved before any text did ------------------------------
@detector("logits_path", "logits", "asking for logprobs changes the output, which would invalidate the logits detectors", "logprobs")
def logits_path(p):
    def perturb(spec, base):
        return p.send(dict(spec, numbers=True)), True, "with logprobs"
    return p.isolate(p.targets(), perturb, rounds=1)


@detector("logits_repeat", "logits", "numbers move run to run, even where the text agrees", "logprobs")
def logits_repeat(p):
    return p.repeats(p.targets(numbers=True))


@detector("logits_batch", "logits", "numbers move with batch size, even where the text agrees", "logprobs")
def logits_batch(p):
    return p.batched(p.targets(numbers=True), 2)


# --- vision: outside the article; a greedy image request isolates the tower from sampling ----------------------------
@detector("vision_repeat", "vision", "the vision tower varies run to run (another image runs between, to defeat any image cache)", "vision")
def vision_repeat(p):
    # A plain repeat that fails is this detector's answer, not a failed control.
    plain = p.repeats([p.image()])
    if plain["verdict"] != "HOLDS":
        return plain
    def perturb(spec, base):
        p.send(p.image(picture=2))
        return p.send(spec), True, "after another image"
    return p.isolate([p.image()], perturb)


@detector("vision_logits", "vision", "the vision tower moves the numbers, even where the text agrees", "vision,logprobs")
def vision_logits(p):
    return p.repeats([p.image(numbers=True)])


@detector("vision_batch_text", "vision", "a text request running beside an image request changes its output", "vision")
def vision_batch_text(p):
    return p.batched([p.image()], 2)


@detector("vision_batch_image", "vision", "two images in flight disturb each other, which a shared tower or embedding batch would do", "vision")
def vision_batch_image(p):
    def perturb(spec, base):
        reply, comps = p.together(spec, [dict(p.image(picture=2), max_tokens=p.cfg.max_tokens * 3)])
        if comps[0]["error"]:
            return None, False, comps[0]["error"]
        stats = overlap(reply, comps[0])
        return reply, stats["cover"] >= BUSY_COVER and stats["switches"] >= BUSY_SWITCHES, stats
    return p.isolate([p.image()], perturb)


def diagnose(found):
    # Causes in the article's terms, read across detectors. A detector's verdict alone can't always name one.
    v = {k: f["verdict"] for k, f in found.items()}
    bad = lambda k: v.get(k) == "BREAKS"
    good = lambda k: v.get(k) == "HOLDS"
    out = []
    if bad("repeat_greedy"):
        out.append("Not run-to-run deterministic even alone and greedy. Fix this first; most other detectors can't isolate anything until then.")
    elif bad("repeat_seeded"):
        out.append("Greedy repeats but seeded doesn't: the sampler isn't replaying from its seed.")
    if bad("seed_interleave"):
        out.append("Sampler state leaks between requests in series.")
    for w in (2, 3, 4):
        g, s = f"batch_greedy_w{w}", f"batch_seeded_w{w}"
        if bad(g):
            out.append(f"Width {w}: numbers depend on batch size (the article's main cause).")
        elif good(g) and bad(s):
            out.append(f"Width {w}: numbers hold, sampling doesn't: sampler state is shared across the batch.")
    if v.get("batch_greedy_w2") == "NOT_REACHED" and v.get("join_mid") == "NOT_REACHED":
        out.append("Requests never ran together: this engine queues, so its HOLDS elsewhere say nothing about overlap.")
    if good("batch_greedy_w2") and bad("join_mid"):
        out.append("Stable batch is fine, but a batch growing mid-decode isn't.")
    if good("batch_greedy_w2") and bad("leave_mid"):
        out.append("Stable batch is fine, but a batch shrinking mid-decode isn't.")
    if good("batch_greedy_w2") and bad("prefill_mix"):
        out.append("Decode-only batches are fine, but another request's prefill disturbs them.")
    if bad("prefix_cache"):
        out.append("Prefix reuse changes output: KV cache layout depends on how the prompt was split.")
    if bad("idle_gap") or bad("after_traffic"):
        out.append("Output depends on elapsed time or request history: something adaptive is steering the run.")
    if bad("logits_path"):
        out.append("Requesting logprobs changes the output, so the logits detectors measure a different path here.")
    if v.get("logits_repeat") == "DRIFT" or v.get("logits_batch") == "DRIFT":
        out.append("Text matched but logprobs moved: a longer run can still diverge when a near-tie flips.")
    if bad("vision_repeat") or v.get("vision_logits") in ("BREAKS", "DRIFT"):
        tail = " The tower is the suspect, since text repeats." if good("repeat_greedy") else ""
        out.append("Image requests don't repeat alone." + tail)
    if good("vision_repeat") and (bad("vision_batch_text") or bad("vision_batch_image")):
        out.append("Image requests repeat alone but change under overlap.")
    return out or ["No cause detected in the situations reached."]


def sees_images(p):
    # Asked rather than looked up: nothing in the chat completions API says whether a model takes images.
    content = [{"type": "text", "text": "What single color fills this image? Answer with one word."},
               {"type": "image_url", "image_url": {"url": png(1, 64, solid=bytes([220, 20, 20]))}}]
    r = p.send(dict(p.spec("", tokens=1000), messages=[{"role": "user", "content": content}]))
    return not r["error"] and "red" in r["text"].lower()


def run_model(cfg, wanted):
    p = Probe(cfg)
    model = cfg.model
    say(f"=== {model} at {p.url} {time.strftime('%H:%M:%S')}")
    a = p.send(p.spec(PROMPTS[0], 7, tokens=80))
    if a["error"]:
        # Some servers refuse the option that reports cache hits, which only the prefix cache detector reads.
        p.usage_option = False
        a = p.send(p.spec(PROMPTS[0], 7, tokens=80))
    if a["error"]:
        say("  request failed:", a["error"])
        return None
    b = p.send(p.spec(PROMPTS[0], 8, tokens=80))
    numbers = p.send(p.spec(PROMPTS[0], 7, numbers=True, tokens=20))
    p.caps = {"seed": a["text"] != b["text"], "logprobs": bool(numbers["lps"]), "vision": sees_images(p)}
    say("  capabilities:", p.caps)
    found = {}
    for name in wanted:
        d = DETECTORS[name]
        missing = [n for n in d["needs"].split(",") if n and not p.caps.get(n)]
        started = time.time()
        if missing:
            f = p.finding("SKIP", [f"needs {', '.join(missing)}"])
        else:
            try:
                f = d["fn"](p)
            except Exception as e:
                f = p.finding("ERROR", [f"{type(e).__name__}: {e}"])
        f["seconds"] = round(time.time() - started)
        found[name] = f
        say(f"  {name:20} {f['verdict']:12} {f['seconds']:>5}s  {'; '.join(f['notes'])[:150]}")
    for line in diagnose(found):
        say("  ->", line)
    os.makedirs(cfg.out, exist_ok=True)
    with open(os.path.join(cfg.out, model.replace("/", "__") + ".json"), "w") as f:
        json.dump({"model": model, "caps": p.caps, "found": found, "causes": {k: DETECTORS[k]["cause"] for k in found},
                   "diagnosis": diagnose(found)}, f, indent=1)
    return found


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("url", nargs="?", help="chat completions endpoint or its base")
    ap.add_argument("api_key", nargs="?", help='the key, or "none"')
    ap.add_argument("model", nargs="?")
    ap.add_argument("--out", default="determinism", help="folder for the full results")
    ap.add_argument("--rounds", type=int, default=3, help="perturbed repeats per prompt")
    ap.add_argument("--prompts", type=int, default=2, choices=[1, 2, 3])
    ap.add_argument("--max-tokens", type=int, default=300)
    ap.add_argument("--widths", default="2,3", help="simultaneous requests for the batch detectors")
    ap.add_argument("--long-lines", type=int, default=100, help="length of the long prompts, about 30 tokens a line")
    ap.add_argument("--idle", type=int, default=30, help="seconds for idle_gap")
    ap.add_argument("--traffic", type=int, default=3, help="requests between repeats for after_traffic")
    ap.add_argument("--only", default="", help="comma list of detector names, name parts or groups")
    ap.add_argument("--skip", default="", help="same form as --only")
    ap.add_argument("--quick", action="store_true", help="one prompt, two rounds, width 2: shows what to look at, not a verdict")
    ap.add_argument("--list", action="store_true")
    cfg = ap.parse_args()
    if cfg.quick:
        cfg.rounds, cfg.prompts, cfg.widths = 2, 1, "2"
    for w in sorted(int(x) for x in cfg.widths.split(",")):
        batch_detector(w)
    order = {"series": 0, "state": 1, "batch": 2, "logits": 3, "vision": 4}
    names = sorted(DETECTORS, key=lambda n: (order[DETECTORS[n]["group"]], not n.startswith("batch_"), n))
    if cfg.list:
        for n in names:
            say(f"{n:20} {DETECTORS[n]['group']:7} {DETECTORS[n]['cause']}")
        return
    if not (cfg.url and cfg.api_key and cfg.model):
        ap.error("needs URL API_KEY MODEL, or --list")
    def picks(text, name):
        return any(t and (t in name or t == DETECTORS[name]["group"]) for t in text.split(","))
    wanted = [n for n in names if (not cfg.only or picks(cfg.only, n)) and not picks(cfg.skip, n)]
    run_model(cfg, wanted)
    say("DONE", time.strftime("%H:%M:%S"))


if __name__ == "__main__":
    main()
