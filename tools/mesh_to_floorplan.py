#!/usr/bin/env python3
"""Derive a 2D wall floor plan from a triangle OBJ room mesh.

The reconstruction is deliberately geometry-only: it needs neither the Quest
session JSON nor manually traced rooms. It detects the floor, cuts vertical
triangles at several heights, keeps geometry that persists across those cuts,
and converts the result to cleaned wall contours.

Outputs:
  <name>_floorplan.svg   filled wall plan, suitable for viewing/import
  <name>_floorplan.json  world-space contours and reconstruction metadata

Example:
  python tools/mesh_to_floorplan.py Temp/scan_roommesh.obj
  python tools/mesh_to_floorplan.py scan.obj -o plan --resolution 0.04
  python tools/mesh_to_floorplan.py scan.obj --floor-y 0 --ceiling-y 2.5
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from collections import defaultdict, deque
from pathlib import Path
from typing import Iterable, Iterator, Sequence


Vec3 = tuple[float, float, float]
Face = tuple[int, int, int]
Cell = tuple[int, int]
Point2 = tuple[int, int]
Edge = tuple[Point2, Point2]


def parse_obj(path: Path) -> tuple[list[Vec3], list[Face]]:
    vertices: list[Vec3] = []
    faces: list[Face] = []
    with path.open("r", encoding="utf-8", errors="replace") as stream:
        for line_number, raw in enumerate(stream, 1):
            line = raw.lstrip()
            if line.startswith("v "):
                parts = line.split()
                if len(parts) < 4:
                    continue
                vertices.append((float(parts[1]), float(parts[2]), float(parts[3])))
            elif line.startswith("f "):
                tokens = line.split()[1:]
                indices: list[int] = []
                for token in tokens:
                    head = token.split("/", 1)[0]
                    if not head:
                        continue
                    value = int(head)
                    index = value - 1 if value > 0 else len(vertices) + value
                    if not 0 <= index < len(vertices):
                        raise ValueError(
                            f"Invalid vertex index at line {line_number}: {value}"
                        )
                    indices.append(index)
                for i in range(1, len(indices) - 1):
                    faces.append((indices[0], indices[i], indices[i + 1]))
    if not vertices or not faces:
        raise ValueError("OBJ contains no usable vertices and triangular faces")
    return vertices, faces


def triangle_normal_area(a: Vec3, b: Vec3, c: Vec3) -> tuple[Vec3, float]:
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    nx = uy * vz - uz * vy
    ny = uz * vx - ux * vz
    nz = ux * vy - uy * vx
    length = math.sqrt(nx * nx + ny * ny + nz * nz)
    if length <= 1e-12:
        return (0.0, 0.0, 0.0), 0.0
    return (nx / length, ny / length, nz / length), length * 0.5


def detect_floor_and_ceiling(
    vertices: Sequence[Vec3], faces: Sequence[Face], bin_size: float = 0.05
) -> tuple[float, float | None, list[dict[str, float]]]:
    """Find dominant near-horizontal surfaces using area-weighted peaks."""
    bins: dict[int, float] = defaultdict(float)
    min_y = min(v[1] for v in vertices)
    max_y = max(v[1] for v in vertices)
    for ia, ib, ic in faces:
        a, b, c = vertices[ia], vertices[ib], vertices[ic]
        normal, area = triangle_normal_area(a, b, c)
        if area <= 0.0 or abs(normal[1]) < 0.82:
            continue
        y = (a[1] + b[1] + c[1]) / 3.0
        bins[round(y / bin_size)] += area
    if not bins:
        raise ValueError("Could not find horizontal surfaces for floor detection")

    smooth: dict[int, float] = {}
    for key in bins:
        smooth[key] = sum(bins.get(key + offset, 0.0) for offset in (-1, 0, 1))

    def local_peaks() -> list[int]:
        result = []
        for key, value in smooth.items():
            if all(value >= smooth.get(key + offset, 0.0)
                   for offset in (-2, -1, 1, 2)):
                result.append(key)
        return result or list(smooth)

    peaks = local_peaks()
    lower_limit = min_y + (max_y - min_y) * 0.55
    floor_candidates = [key for key in peaks if key * bin_size <= lower_limit]
    if not floor_candidates:
        floor_candidates = peaks
    floor_key = max(floor_candidates, key=lambda key: smooth[key])

    def refine(key: int) -> float:
        weighted = [(candidate * bin_size, bins.get(candidate, 0.0))
                    for candidate in range(key - 2, key + 3)]
        total = sum(weight for _, weight in weighted)
        return (sum(y * weight for y, weight in weighted) / total
                if total > 0.0 else key * bin_size)

    floor_y = refine(floor_key)
    ceiling_candidates = [
        key for key in peaks
        if floor_y + 1.75 <= key * bin_size <= floor_y + 4.5
    ]
    ceiling_y = (refine(max(ceiling_candidates, key=lambda key: smooth[key]))
                 if ceiling_candidates else None)
    peak_info = [
        {"y": round(key * bin_size, 4), "weightedArea": round(smooth[key], 4)}
        for key in sorted(peaks, key=lambda item: smooth[item], reverse=True)[:12]
    ]
    return floor_y, ceiling_y, peak_info


def parse_offsets(value: str) -> list[float]:
    result = [float(item.strip()) for item in value.split(",") if item.strip()]
    if not result:
        raise argparse.ArgumentTypeError("at least one comma-separated offset is required")
    return sorted(set(result))


def default_slice_heights(floor_y: float, ceiling_y: float | None) -> list[float]:
    # A narrow band around 1.8 m suppresses tables, sofas and most shelving.
    # Multiple nearby cuts are still preferable to one exact plane because a
    # reconstructed mesh contains local holes and uneven triangle boundaries.
    center = 1.80
    if ceiling_y is not None and 1.8 <= ceiling_y - floor_y <= 4.5:
        center = min(center, ceiling_y - floor_y - 0.35)
    return [floor_y + center + offset for offset in (-0.15, -0.075, 0.0, 0.075, 0.15)]


def unique_points(points: Iterable[Vec3], epsilon: float = 1e-7) -> list[Vec3]:
    result: list[Vec3] = []
    epsilon_sq = epsilon * epsilon
    for point in points:
        if all((point[0] - other[0]) ** 2 +
               (point[1] - other[1]) ** 2 +
               (point[2] - other[2]) ** 2 > epsilon_sq for other in result):
            result.append(point)
    return result


def triangle_plane_segment(a: Vec3, b: Vec3, c: Vec3, height: float) -> tuple[Vec3, Vec3] | None:
    intersections: list[Vec3] = []
    epsilon = 1e-7
    for p, q in ((a, b), (b, c), (c, a)):
        dp, dq = p[1] - height, q[1] - height
        if abs(dp) <= epsilon and abs(dq) <= epsilon:
            intersections.extend((p, q))
        elif abs(dp) <= epsilon:
            intersections.append(p)
        elif abs(dq) <= epsilon:
            intersections.append(q)
        elif (dp < 0.0 < dq) or (dq < 0.0 < dp):
            t = dp / (dp - dq)
            intersections.append((
                p[0] + (q[0] - p[0]) * t,
                height,
                p[2] + (q[2] - p[2]) * t,
            ))
    intersections = unique_points(intersections)
    if len(intersections) < 2:
        return None
    if len(intersections) == 2:
        return intersections[0], intersections[1]
    # Degenerate plane contact: retain the farthest pair.
    best: tuple[Vec3, Vec3] | None = None
    best_distance = -1.0
    for i, first in enumerate(intersections):
        for second in intersections[i + 1:]:
            distance = (first[0] - second[0]) ** 2 + (first[2] - second[2]) ** 2
            if distance > best_distance:
                best, best_distance = (first, second), distance
    return best


def disk_offsets(radius: int) -> list[Cell]:
    if radius <= 0:
        return [(0, 0)]
    return [
        (x, z) for z in range(-radius, radius + 1)
        for x in range(-radius, radius + 1)
        if x * x + z * z <= radius * radius
    ]


def rasterize_mesh_slices(
    vertices: Sequence[Vec3],
    faces: Sequence[Face],
    heights: Sequence[float],
    resolution: float,
    wall_width: float,
    max_normal_y: float,
    min_persistence: float,
    margin: float,
) -> tuple[set[Cell], dict[str, float | int], tuple[float, float, int, int]]:
    min_x = min(v[0] for v in vertices) - margin
    max_x = max(v[0] for v in vertices) + margin
    min_z = min(v[2] for v in vertices) - margin
    max_z = max(v[2] for v in vertices) + margin
    width = max(1, math.ceil((max_x - min_x) / resolution))
    depth = max(1, math.ceil((max_z - min_z) / resolution))
    masks = [0] * (width * depth)
    radius = max(0, math.ceil((wall_width * 0.5) / resolution))
    offsets = disk_offsets(radius)
    segments = 0

    def mark(x: float, z: float, bit: int) -> None:
        ix = math.floor((x - min_x) / resolution)
        iz = math.floor((z - min_z) / resolution)
        for dx, dz in offsets:
            px, pz = ix + dx, iz + dz
            if 0 <= px < width and 0 <= pz < depth:
                masks[pz * width + px] |= bit

    for ia, ib, ic in faces:
        a, b, c = vertices[ia], vertices[ib], vertices[ic]
        normal, area = triangle_normal_area(a, b, c)
        if area <= 0.0 or abs(normal[1]) > max_normal_y:
            continue
        low = min(a[1], b[1], c[1])
        high = max(a[1], b[1], c[1])
        for slice_index, height in enumerate(heights):
            if not low - 1e-7 <= height <= high + 1e-7:
                continue
            segment = triangle_plane_segment(a, b, c, height)
            if segment is None:
                continue
            first, second = segment
            length = math.hypot(second[0] - first[0], second[2] - first[2])
            if length < resolution * 0.12:
                continue
            steps = max(1, math.ceil(length / (resolution * 0.35)))
            bit = 1 << slice_index
            for step in range(steps + 1):
                t = step / steps
                mark(first[0] + (second[0] - first[0]) * t,
                     first[2] + (second[2] - first[2]) * t, bit)
            segments += 1

    required_votes = max(1, math.ceil(len(heights) * min_persistence))
    cells = {
        (index % width, index // width)
        for index, mask in enumerate(masks)
        if mask.bit_count() >= required_votes
    }
    stats: dict[str, float | int] = {
        "sliceSegments": segments,
        "requiredSliceVotes": required_votes,
        "rawWallCells": len(cells),
    }
    return cells, stats, (min_x, min_z, width, depth)


def dilate(cells: set[Cell], radius: int) -> set[Cell]:
    if radius <= 0 or not cells:
        return set(cells)
    offsets = disk_offsets(radius)
    return {(x + dx, z + dz) for x, z in cells for dx, dz in offsets}


def erode(cells: set[Cell], radius: int) -> set[Cell]:
    if radius <= 0 or not cells:
        return set(cells)
    offsets = disk_offsets(radius)
    return {
        (x, z) for x, z in cells
        if all((x + dx, z + dz) in cells for dx, dz in offsets)
    }


def close_gaps(cells: set[Cell], radius: int) -> set[Cell]:
    return erode(dilate(cells, radius), radius) if radius > 0 else set(cells)


def remove_small_components(
    cells: set[Cell], resolution: float, min_cells: int, min_extent: float
) -> tuple[set[Cell], int]:
    remaining = set(cells)
    kept: set[Cell] = set()
    removed = 0
    neighbours = [(dx, dz) for dz in (-1, 0, 1) for dx in (-1, 0, 1)
                  if dx or dz]
    while remaining:
        seed = remaining.pop()
        component = {seed}
        queue = deque([seed])
        while queue:
            x, z = queue.popleft()
            for dx, dz in neighbours:
                candidate = (x + dx, z + dz)
                if candidate in remaining:
                    remaining.remove(candidate)
                    component.add(candidate)
                    queue.append(candidate)
        xs = [cell[0] for cell in component]
        zs = [cell[1] for cell in component]
        extent = max(max(xs) - min(xs) + 1, max(zs) - min(zs) + 1) * resolution
        if len(component) >= min_cells and extent >= min_extent:
            kept.update(component)
        else:
            removed += 1
    return kept, removed


def boundary_edges(cells: set[Cell]) -> set[Edge]:
    edges: set[Edge] = set()
    for x, z in cells:
        if (x, z - 1) not in cells:
            edges.add(((x, z), (x + 1, z)))
        if (x + 1, z) not in cells:
            edges.add(((x + 1, z), (x + 1, z + 1)))
        if (x, z + 1) not in cells:
            edges.add(((x + 1, z + 1), (x, z + 1)))
        if (x - 1, z) not in cells:
            edges.add(((x, z + 1), (x, z)))
    return edges


def direction(first: Point2, second: Point2) -> int:
    dx, dz = second[0] - first[0], second[1] - first[1]
    return {(1, 0): 0, (0, 1): 1, (-1, 0): 2, (0, -1): 3}[(dx, dz)]


def simplify_collinear(points: list[Point2]) -> list[Point2]:
    if len(points) < 4:
        return points
    result = points[:]
    changed = True
    while changed and len(result) >= 4:
        changed = False
        simplified: list[Point2] = []
        count = len(result)
        for i, point in enumerate(result):
            previous = result[(i - 1) % count]
            following = result[(i + 1) % count]
            ax, az = point[0] - previous[0], point[1] - previous[1]
            bx, bz = following[0] - point[0], following[1] - point[1]
            if ax * bz - az * bx == 0 and ax * bx + az * bz > 0:
                changed = True
            else:
                simplified.append(point)
        result = simplified
    return result


def trace_contours(cells: set[Cell]) -> list[list[Point2]]:
    edges = boundary_edges(cells)
    outgoing: dict[Point2, set[Point2]] = defaultdict(set)
    for start, end in edges:
        outgoing[start].add(end)
    contours: list[list[Point2]] = []
    while edges:
        first_edge = min(edges)
        start, end = first_edge
        points = [start]
        current_start, current_end = start, end
        closed = False
        while True:
            edge = (current_start, current_end)
            if edge not in edges:
                break
            edges.remove(edge)
            outgoing[current_start].discard(current_end)
            points.append(current_end)
            if current_end == start:
                closed = True
                break
            candidates = [candidate for candidate in outgoing.get(current_end, ())
                          if (current_end, candidate) in edges]
            if not candidates:
                break
            incoming_direction = direction(current_start, current_end)

            def turn_priority(candidate: Point2) -> tuple[int, int]:
                delta = (direction(current_end, candidate) - incoming_direction) % 4
                # At diagonal contacts, following the left turn keeps the two
                # cell contours separate instead of crossing between them.
                order = {1: 0, 0: 1, 3: 2, 2: 3}
                return order[delta], direction(current_end, candidate)

            following = min(candidates, key=turn_priority)
            current_start, current_end = current_end, following
        if closed and len(points) >= 5:
            contours.append(simplify_collinear(points[:-1]))
    return contours


def signed_area(points: Sequence[tuple[float, float]]) -> float:
    return 0.5 * sum(
        points[i][0] * points[(i + 1) % len(points)][1]
        - points[(i + 1) % len(points)][0] * points[i][1]
        for i in range(len(points))
    )


def world_contours(
    contours: Sequence[Sequence[Point2]], origin_x: float, origin_z: float,
    resolution: float
) -> list[list[tuple[float, float]]]:
    return [[
        (origin_x + x * resolution, origin_z + z * resolution)
        for x, z in contour
    ] for contour in contours]


def output_base(input_path: Path, requested: str | None) -> Path:
    if requested:
        path = Path(requested)
        return path.with_suffix("") if path.suffix.lower() in {".svg", ".json"} else path
    stem = input_path.stem
    if stem.endswith("_roommesh"):
        stem = stem[:-9]
    return input_path.with_name(stem + "_floorplan")


def write_json(
    path: Path,
    source: Path,
    contours: Sequence[Sequence[tuple[float, float]]],
    cells: set[Cell],
    origin_x: float,
    origin_z: float,
    resolution: float,
    floor_y: float,
    ceiling_y: float | None,
    slice_heights: Sequence[float],
    parameters: dict[str, float | int],
    stats: dict[str, float | int],
    peaks: Sequence[dict[str, float]],
) -> None:
    xs = [origin_x + x * resolution for x, _ in cells]
    zs = [origin_z + z * resolution for _, z in cells]
    bounds = {
        "minX": min(xs), "maxX": max(xs) + resolution,
        "minZ": min(zs), "maxZ": max(zs) + resolution,
    }
    encoded_contours = []
    for contour in contours:
        area = signed_area(contour)
        encoded_contours.append({
            "kind": "outer" if area > 0 else "hole",
            "signedAreaSqm": round(area, 4),
            "pointsXZ": [[round(x, 4), round(z, 4)] for x, z in contour],
        })
    data = {
        "format": "quest3-wifiscanner.mesh-floorplan.v1",
        "source": str(source.resolve()),
        "coordinateSystem": {
            "plane": "OBJ X/Z",
            "verticalAxis": "Y",
            "units": "meters",
            "note": "OBJ X was already negated by the Quest exporter",
        },
        "floorY": round(floor_y, 4),
        "ceilingY": round(ceiling_y, 4) if ceiling_y is not None else None,
        "sliceHeightsY": [round(value, 4) for value in slice_heights],
        "bounds": {key: round(value, 4) for key, value in bounds.items()},
        "parameters": parameters,
        "stats": {
            **stats,
            "wallCells": len(cells),
            "wallAreaSqm": round(len(cells) * resolution * resolution, 4),
            "contours": len(contours),
        },
        "horizontalSurfacePeaks": peaks,
        "contours": encoded_contours,
    }
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def svg_number(value: float) -> str:
    return f"{value:.2f}".rstrip("0").rstrip(".")


def write_svg(
    path: Path,
    contours: Sequence[Sequence[tuple[float, float]]],
    source: Path,
    floor_y: float,
    padding: float = 0.25,
) -> None:
    all_points = [point for contour in contours for point in contour]
    min_x = min(point[0] for point in all_points) - padding
    max_x = max(point[0] for point in all_points) + padding
    min_z = min(point[1] for point in all_points) - padding
    max_z = max(point[1] for point in all_points) + padding
    width_mm = (max_x - min_x) * 1000.0
    height_mm = (max_z - min_z) * 1000.0
    commands: list[str] = []
    for contour in contours:
        if len(contour) < 3:
            continue
        first_x = (contour[0][0] - min_x) * 1000.0
        first_y = (max_z - contour[0][1]) * 1000.0
        commands.append(f"M {svg_number(first_x)} {svg_number(first_y)}")
        for x, z in contour[1:]:
            commands.append(
                f"L {svg_number((x - min_x) * 1000.0)} "
                f"{svg_number((max_z - z) * 1000.0)}"
            )
        commands.append("Z")
    title = f"Floor plan reconstructed from {source.name} at y={floor_y:.3f} m"
    svg = (
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        f"<svg xmlns=\"http://www.w3.org/2000/svg\" "
        f"viewBox=\"0 0 {svg_number(width_mm)} {svg_number(height_mm)}\" "
        f"width=\"{svg_number(width_mm)}mm\" height=\"{svg_number(height_mm)}mm\">\n"
        f"  <title>{title}</title>\n"
        f"  <desc>Coordinates are millimetres; source OBJ uses metres in X/Z.</desc>\n"
        f"  <rect width=\"100%\" height=\"100%\" fill=\"white\"/>\n"
        f"  <path d=\"{' '.join(commands)}\" fill=\"#15181f\" "
        f"fill-rule=\"evenodd\" stroke=\"none\"/>\n"
        "</svg>\n"
    )
    path.write_text(svg, encoding="utf-8")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Reconstruct a 2D wall floor plan from an OBJ room mesh."
    )
    parser.add_argument("obj", type=Path, help="input OBJ triangle mesh")
    parser.add_argument("-o", "--output", help="output base path (without extension)")
    parser.add_argument("--floor-y", type=float,
                        help="known floor Y in metres; default: automatic")
    parser.add_argument("--ceiling-y", type=float,
                        help="known ceiling Y in metres; default: automatic")
    parser.add_argument(
        "--slice-offsets", type=parse_offsets,
        help="comma-separated heights above the floor; default: five cuts around 1.8 m",
    )
    parser.add_argument("--resolution", type=float, default=0.05,
                        help="raster cell size in metres (default: 0.05)")
    parser.add_argument("--wall-width", type=float, default=0.12,
                        help="minimum visual wall width in metres (default: 0.12)")
    parser.add_argument("--wall-normal-y", type=float, default=0.50,
                        help="maximum absolute wall normal Y (default: 0.50)")
    parser.add_argument("--min-persistence", type=float, default=0.60,
                        help="fraction of height slices required (default: 0.60)")
    parser.add_argument("--close-gap", type=float, default=0.16,
                        help="bridge wall gaps up to approximately this size (default: 0.16 m)")
    parser.add_argument("--min-component-cells", type=int, default=10,
                        help="discard smaller isolated raster components (default: 10)")
    parser.add_argument("--min-component-length", type=float, default=0.40,
                        help="discard components shorter than this (default: 0.40 m)")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    source = args.obj.resolve()
    if not source.is_file():
        print(f"error: input file does not exist: {source}", file=sys.stderr)
        return 2
    if args.resolution <= 0.0 or args.wall_width <= 0.0:
        print("error: resolution and wall width must be positive", file=sys.stderr)
        return 2
    if not 0.0 < args.min_persistence <= 1.0:
        print("error: min-persistence must be in (0, 1]", file=sys.stderr)
        return 2

    print(f"Reading {source} ...")
    vertices, faces = parse_obj(source)
    detected_floor, detected_ceiling, peaks = detect_floor_and_ceiling(vertices, faces)
    floor_y = args.floor_y if args.floor_y is not None else detected_floor
    ceiling_y = args.ceiling_y if args.ceiling_y is not None else detected_ceiling
    heights = ([floor_y + offset for offset in args.slice_offsets]
               if args.slice_offsets else default_slice_heights(floor_y, ceiling_y))
    print(
        f"Mesh: {len(vertices):,} vertices, {len(faces):,} triangles\n"
        f"Floor Y: {floor_y:.3f} m"
        + (f", ceiling Y: {ceiling_y:.3f} m" if ceiling_y is not None else "")
        + "\nSlices: " + ", ".join(f"{height:.3f}" for height in heights)
    )

    margin = max(0.5, args.close_gap + args.wall_width)
    cells, stats, raster = rasterize_mesh_slices(
        vertices, faces, heights, args.resolution, args.wall_width,
        args.wall_normal_y, args.min_persistence, margin,
    )
    close_radius = max(0, math.ceil(args.close_gap / (2.0 * args.resolution)))
    cells = close_gaps(cells, close_radius)
    cells, removed_components = remove_small_components(
        cells, args.resolution, args.min_component_cells,
        args.min_component_length,
    )
    if not cells:
        print(
            "error: no persistent wall geometry remained; try lowering "
            "--min-persistence or specify --floor-y",
            file=sys.stderr,
        )
        return 1
    stats["closedWallCells"] = len(cells)
    stats["removedComponents"] = removed_components
    stats["meshVertices"] = len(vertices)
    stats["meshTriangles"] = len(faces)

    origin_x, origin_z, _, _ = raster
    contours_grid = trace_contours(cells)
    contours = world_contours(contours_grid, origin_x, origin_z, args.resolution)
    if not contours:
        print("error: wall raster could not be converted to closed contours", file=sys.stderr)
        return 1

    base = output_base(source, args.output)
    base.parent.mkdir(parents=True, exist_ok=True)
    svg_path = base.with_suffix(".svg")
    json_path = base.with_suffix(".json")
    parameters: dict[str, float | int] = {
        "resolution": args.resolution,
        "wallWidth": args.wall_width,
        "wallNormalY": args.wall_normal_y,
        "minPersistence": args.min_persistence,
        "closeGap": args.close_gap,
        "minComponentCells": args.min_component_cells,
        "minComponentLength": args.min_component_length,
    }
    write_svg(svg_path, contours, source, floor_y)
    write_json(
        json_path, source, contours, cells, origin_x, origin_z,
        args.resolution, floor_y, ceiling_y, heights, parameters, stats, peaks,
    )
    print(
        f"Floor plan: {len(cells):,} wall cells, {len(contours):,} contours\n"
        f"SVG:  {svg_path}\nJSON: {json_path}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
