FeatureScript 3044;
import(path : "onshape/std/common.fs", version : "3044.0");

/**
 * Versatz Kurven
 *
 * Versetzt ALLE gewaehlten Skizzenkurven (Linien, Boegen, Kreise) einer Skizze
 * (z. B. DXF-Import) in einem Schritt um einen Abstand nach innen/aussen.
 * Verbundene Kurven werden automatisch zu Ketten zusammengefasst, getrennte
 * Ketten werden einzeln versetzt. Ergebnis ist eine neue Skizze.
 *
 * Einschraenkungen: nur Linien/Boegen/Kreise (keine Splines), alle Kurven in
 * derselben Skizzenebene.
 */

export enum VersatzSeite
{
    annotation { "Name" : "Innen" }
    INNEN,
    annotation { "Name" : "Außen" }
    AUSSEN,
    annotation { "Name" : "Beide Seiten" }
    BEIDE
}

annotation { "Feature Type Name" : "Versatz Kurven", "Feature Type Description" : "Versetzt alle gewaehlten Skizzenkurven nach innen/aussen" }
export const versatzKurven = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Skizzenkurven", "Filter" : EntityType.EDGE && SketchObject.YES && ConstructionObject.NO }
        definition.edges is Query;

        annotation { "Name" : "Abstand" }
        isLength(definition.distance, NONNEGATIVE_LENGTH_BOUNDS);

        annotation { "Name" : "Seite", "UIHint" : UIHint.SHOW_LABEL }
        definition.side is VersatzSeite;

        annotation { "Name" : "Richtung umkehren (offene Ketten)", "UIHint" : UIHint.OPPOSITE_DIRECTION }
        definition.flip is boolean;

        annotation { "Name" : "Verbindungstoleranz" }
        isLength(definition.tol, { (millimeter) : [1e-4, 0.01, 1] } as LengthBoundSpec);
    }
    {
        const edgeList = evaluateQuery(context, definition.edges);
        if (size(edgeList) == 0)
            throw regenError("Keine Kurven gewählt.", ["edges"]);

        const plane = evOwnerSketchPlane(context, { "entity" : edgeList[0] });
        const yAxis = cross(plane.normal, plane.x);
        const tol = definition.tol / meter;
        const d = definition.distance / meter;

        // ---- Kanten einlesen (2D, in Metern) ----
        var segs = [];
        var circles = [];
        for (var e in edgeList)
        {
            const curve = evCurveDefinition(context, { "edge" : e });
            const l0 = evEdgeTangentLine(context, { "edge" : e, "parameter" : 0 });
            const l1 = evEdgeTangentLine(context, { "edge" : e, "parameter" : 1 });
            const p0 = to2D(l0.origin, plane, yAxis);
            const p1 = to2D(l1.origin, plane, yAxis);
            const t0 = dir2D(l0.direction, plane, yAxis);
            const t1 = dir2D(l1.direction, plane, yAxis);

            if (curve is Line)
            {
                segs = append(segs, { "kind" : "line", "p0" : p0, "p1" : p1, "t0" : t0, "t1" : t1 });
            }
            else if (curve is Circle)
            {
                const c = to2D(curve.coordSystem.origin, plane, yAxis);
                const r = curve.radius / meter;
                if (norm2(p0 - p1) < tol)
                {
                    circles = append(circles, { "c" : c, "r" : r });
                }
                else
                {
                    const ccw = cross2(p0 - c, t0) > 0;
                    segs = append(segs, { "kind" : "arc", "p0" : p0, "p1" : p1, "t0" : t0, "t1" : t1,
                                "c" : c, "r" : r, "ccw" : ccw });
                }
            }
            else
            {
                reportFeatureWarning(context, id, "Kurve ohne Linie/Bogen/Kreis wurde übersprungen (z. B. Spline).");
            }
        }

        // ---- Ketten bilden ----
        var used = makeArray(size(segs), false);
        var chains = [];
        for (var i = 0; i < size(segs); i += 1)
        {
            if (used[i])
                continue;
            used[i] = true;
            var chain = [segs[i]];
            var grew = true;
            while (grew)
            {
                grew = false;
                for (var j = 0; j < size(segs); j += 1)
                {
                    if (used[j])
                        continue;
                    const tail = chain[size(chain) - 1].p1;
                    const head = chain[0].p0;
                    var cand = segs[j];
                    if (norm2(cand.p0 - tail) < tol)
                        chain = append(chain, cand);
                    else if (norm2(cand.p1 - tail) < tol)
                        chain = append(chain, reverseSeg(cand));
                    else if (norm2(cand.p1 - head) < tol)
                        chain = concatenateArrays([[cand], chain]);
                    else if (norm2(cand.p0 - head) < tol)
                        chain = concatenateArrays([[reverseSeg(cand)], chain]);
                    else
                        continue;
                    used[j] = true;
                    grew = true;
                }
            }
            chains = append(chains, chain);
        }

        // ---- Versatz ----
        const sketch = newSketchOnPlane(context, id + "sketch", { "sketchPlane" : plane });
        var counter = 0;

        var offsets = [];
        if (definition.side == VersatzSeite.INNEN || definition.side == VersatzSeite.BEIDE)
            offsets = append(offsets, d);
        if (definition.side == VersatzSeite.AUSSEN || definition.side == VersatzSeite.BEIDE)
            offsets = append(offsets, -d);

        for (var chain0 in chains)
        {
            var chain = chain0;
            const closed = norm2(chain[0].p0 - chain[size(chain) - 1].p1) < tol;
            if (closed && signedArea(chain) < 0)
                chain = reverseChain(chain);

            for (var s0 in offsets)
            {
                // positive s = links der Laufrichtung (= innen bei geschlossenen, CCW-Ketten)
                const s = (!closed && definition.flip) ? -s0 : s0;
                const res = offsetChain(chain, s, closed, tol);
                if (res.warn != undefined)
                    reportFeatureWarning(context, id, res.warn);
                for (var item in res.items)
                {
                    counter += 1;
                    const sid = "e" ~ counter;
                    if (item.kind == "line")
                        skLineSegment(sketch, sid, { "start" : item.a * meter, "end" : item.b * meter });
                    else if (item.kind == "arc")
                        skArc(sketch, sid, { "start" : item.a * meter, "mid" : item.m * meter, "end" : item.b * meter });
                }
            }
        }

        // ---- Vollkreise ----
        for (var ci in circles)
        {
            for (var s0 in offsets)
            {
                // Kreis wird wie eine CCW-Kette behandelt: innen = kleiner
                const rr = ci.r - s0;
                if (rr <= tol)
                {
                    reportFeatureWarning(context, id, "Ein Kreis verschwindet beim Versatz (Radius <= 0).");
                    continue;
                }
                counter += 1;
                skCircle(sketch, "c" ~ counter, { "center" : ci.c * meter, "radius" : rr * meter });
            }
        }

        skSolve(sketch);
    });

// ---------------------------------------------------------------- Helfer

function to2D(p is Vector, plane is Plane, yAxis is Vector) returns Vector
{
    const v = p - plane.origin;
    return vector(dot(v, plane.x) / meter, dot(v, yAxis) / meter);
}

function dir2D(v is Vector, plane is Plane, yAxis is Vector) returns Vector
{
    const w = normalize(v);
    return vector(dot(w, plane.x), dot(w, yAxis));
}

function cross2(a is Vector, b is Vector) returns number
{
    return a[0] * b[1] - a[1] * b[0];
}

function norm2(v is Vector) returns number
{
    return sqrt(v[0] * v[0] + v[1] * v[1]);
}

function reverseSeg(s is map) returns map
{
    var r = s;
    r.p0 = s.p1;
    r.p1 = s.p0;
    r.t0 = -s.t1;
    r.t1 = -s.t0;
    if (s.kind == "arc")
        r.ccw = !s.ccw;
    return r;
}

function reverseChain(chain is array) returns array
{
    var out = [];
    for (var i = size(chain) - 1; i >= 0; i -= 1)
        out = append(out, reverseSeg(chain[i]));
    return out;
}

// Vorzeichenbehaftete Flaeche (Shoelace, Boegen werden abgetastet)
function signedArea(chain is array) returns number
{
    var pts = [];
    for (var s in chain)
    {
        pts = append(pts, s.p0);
        if (s.kind == "arc")
        {
            const a0 = atan2(s.p0[1] - s.c[1], s.p0[0] - s.c[0]) / radian;
            const sw = arcSweep(s);
            for (var k = 1; k < 16; k += 1)
            {
                const a = a0 + sw * k / 16;
                pts = append(pts, vector(s.c[0] + s.r * cos(a * radian), s.c[1] + s.r * sin(a * radian)));
            }
        }
    }
    var area = 0;
    for (var i = 0; i < size(pts); i += 1)
    {
        const p = pts[i];
        const q = pts[(i + 1) % size(pts)];
        area += p[0] * q[1] - q[0] * p[1];
    }
    return area / 2;
}

// Ueberstrichener Winkel (Radiant, vorzeichenbehaftet) von p0 nach p1
function arcSweep(s is map) returns number
{
    const a0 = atan2(s.p0[1] - s.c[1], s.p0[0] - s.c[0]) / radian;
    const a1 = atan2(s.p1[1] - s.c[1], s.p1[0] - s.c[0]) / radian;
    if (s.ccw)
        return modPositive(a1 - a0, 2 * PI);
    return -modPositive(a0 - a1, 2 * PI);
}

function modPositive(x is number, m is number) returns number
{
    var r = x - m * floor(x / m);
    return r;
}

// Versetzte, unbegrenzte Kurve eines Segments (s > 0: links der Laufrichtung)
function offsetCurveOf(seg is map, s is number) returns map
{
    if (seg.kind == "line")
    {
        const n = vector(-seg.t0[1], seg.t0[0]);
        return { "kind" : "line", "a" : seg.p0 + n * s, "u" : seg.t0, "p0" : seg.p0 + n * s, "p1" : seg.p1 + n * s };
    }
    const r = seg.ccw ? seg.r - s : seg.r + s;
    return { "kind" : "arc", "c" : seg.c, "r" : r, "ccw" : seg.ccw };
}

function intersectCurves(a is map, b is map) returns array
{
    if (a.kind == "line" && b.kind == "line")
    {
        const det = cross2(a.u, b.u);
        if (abs(det) < 1e-9)
            return [];
        const t = cross2(b.a - a.a, b.u) / det;
        return [a.a + a.u * t];
    }
    if (a.kind == "arc" && b.kind == "line")
        return intersectCurves(b, a);
    if (a.kind == "line" && b.kind == "arc")
    {
        const w = a.a - b.c;
        const B = a.u[0] * w[0] + a.u[1] * w[1];
        const C = w[0] * w[0] + w[1] * w[1] - b.r * b.r;
        const disc = B * B - C;
        if (disc < 0)
            return [];
        const sq = sqrt(disc);
        return [a.a + a.u * (-B + sq), a.a + a.u * (-B - sq)];
    }
    // Kreis-Kreis
    const dv = b.c - a.c;
    const dd = norm2(dv);
    if (dd < 1e-12 || dd > a.r + b.r || dd < abs(a.r - b.r))
        return [];
    const x = (dd * dd + a.r * a.r - b.r * b.r) / (2 * dd);
    const h2 = a.r * a.r - x * x;
    const h = h2 > 0 ? sqrt(h2) : 0;
    const ex = dv / dd;
    const ey = vector(-ex[1], ex[0]);
    const m = a.c + ex * x;
    return [m + ey * h, m - ey * h];
}

function nearest(cands is array, ref is Vector) returns Vector
{
    var best = cands[0];
    for (var c in cands)
        if (norm2(c - ref) < norm2(best - ref))
            best = c;
    return best;
}

// Endpunkt des versetzten Segments am Ursprungspunkt (Start oder Ende)
function offsetEndPoint(seg is map, s is number, atEnd is boolean) returns Vector
{
    const p = atEnd ? seg.p1 : seg.p0;
    const t = atEnd ? seg.t1 : seg.t0;
    const n = vector(-t[1], t[0]);
    return p + n * s;
}

function offsetChain(chain is array, s is number, closed is boolean, tol is number) returns map
{
    const n = size(chain);
    var warn = undefined;
    var curves = [];
    for (var seg in chain)
        curves = append(curves, offsetCurveOf(seg, s));

    // Start-/Endpunkt jedes versetzten Segments
    var starts = [];
    var ends = [];
    for (var seg in chain)
    {
        starts = append(starts, offsetEndPoint(seg, s, false));
        ends = append(ends, offsetEndPoint(seg, s, true));
    }

    var bridges = [];
    const nJoin = closed ? n : n - 1;
    for (var i = 0; i < nJoin; i += 1)
    {
        const j = (i + 1) % n;
        const t1 = chain[i].t1;
        const t2 = chain[j].t0;
        const tangent = abs(cross2(t1, t2)) < 1e-6 && (t1[0] * t2[0] + t1[1] * t2[1]) > 0;
        if (tangent)
        {
            // tangentenstetig: Endpunkte stimmen ueberein
            const m = (ends[i] + starts[j]) / 2;
            ends[i] = m;
            starts[j] = m;
            continue;
        }
        const cands = intersectCurves(curves[i], curves[j]);
        if (size(cands) == 0)
        {
            bridges = append(bridges, { "kind" : "line", "a" : ends[i], "b" : starts[j] });
            warn = "An einer Ecke gab es keinen Schnittpunkt; Ecke wurde mit einer Linie verbunden.";
        }
        else
        {
            const q = nearest(cands, chain[i].p1);
            ends[i] = q;
            starts[j] = q;
        }
    }

    var items = bridges;
    for (var i = 0; i < n; i += 1)
    {
        const seg = chain[i];
        const a = starts[i];
        const b = ends[i];
        if (norm2(a - b) < tol)
            continue;
        if (seg.kind == "line")
        {
            // Segment darf nicht umklappen (zu starker Versatz)
            if ((b - a)[0] * seg.t0[0] + (b - a)[1] * seg.t0[1] <= 0)
            {
                warn = "Ein Segment verschwindet beim Versatz (Abstand zu gross).";
                continue;
            }
            items = append(items, { "kind" : "line", "a" : a, "b" : b });
        }
        else
        {
            const r = curves[i].r;
            if (r <= tol)
            {
                warn = "Ein Bogen verschwindet beim Versatz (Radius <= 0).";
                continue;
            }
            const a0 = atan2(a[1] - seg.c[1], a[0] - seg.c[0]) / radian;
            const a1 = atan2(b[1] - seg.c[1], b[0] - seg.c[0]) / radian;
            const sw = seg.ccw ? modPositive(a1 - a0, 2 * PI) : -modPositive(a0 - a1, 2 * PI);
            const am = a0 + sw / 2;
            const m = vector(seg.c[0] + r * cos(am * radian), seg.c[1] + r * sin(am * radian));
            items = append(items, { "kind" : "arc", "a" : a, "m" : m, "b" : b });
        }
    }
    return { "items" : items, "warn" : warn };
}
