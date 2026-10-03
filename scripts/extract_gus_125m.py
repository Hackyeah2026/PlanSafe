#!/usr/bin/env python3
"""
GUS NSP 2021 125m Grid Extractor for PlanSafe
Extracts demographic census population cells from the official GUS ATOM Grid_125_XLS.zip
and projects coordinates from EPSG:3035 (ETRS89-LAEA) to EPSG:4326 (WGS84).
"""

import sys
import os
import re
import math
import json
import zipfile
import argparse
import xml.etree.ElementTree as ET

# GRS80 ellipsoid parameters for EPSG:3035
A = 6378137.0
F = 1.0 / 298.257222101
E2 = 2 * F - F * F
E = math.sqrt(E2)
PHI0 = math.radians(52.0)
LAM0 = math.radians(10.0)
X0 = 4321000.0
Y0 = 3210000.0

def _q_func(p):
    sinp = math.sin(p)
    return (1.0 - E2) * (sinp / (1.0 - E2 * sinp**2) - (1.0 / (2.0 * E)) * math.log((1.0 - E * sinp) / (1.0 + E * sinp)))

Q0 = _q_func(PHI0)
QP = _q_func(math.pi / 2.0)
BETA0 = math.asin(Q0 / QP)
M0 = math.cos(PHI0) / math.sqrt(1.0 - E2 * math.sin(PHI0)**2)
R_Q = A * math.sqrt(QP / 2.0)
D = A * M0 / (R_Q * math.cos(BETA0))

def forward_epsg3035(lat_deg, lon_deg):
    """Converts WGS84 (lat, lon in degrees) to EPSG:3035 (easting, northing in meters)."""
    phi = math.radians(lat_deg)
    lam = math.radians(lon_deg)
    q = _q_func(phi)
    beta = math.asin(max(-1.0, min(1.0, q / QP)))
    b = 1.0 + math.sin(BETA0) * math.sin(beta) + math.cos(BETA0) * math.cos(beta) * math.cos(lam - LAM0)
    b = max(b, 1e-12)
    s = math.sqrt(2.0 / b)
    x = X0 + (R_Q * D) * (math.cos(beta) * math.sin(lam - LAM0)) * s
    y = Y0 + (R_Q / D) * (math.cos(BETA0) * math.sin(beta) - math.sin(BETA0) * math.cos(beta) * math.cos(lam - LAM0)) * s
    return x, y

def inverse_epsg3035(x, y):
    """Converts EPSG:3035 (easting, northing in meters) to WGS84 (lat, lon in degrees)."""
    x_hat = (x - X0) / D
    y_hat = (y - Y0) * D
    rho = math.hypot(x_hat, y_hat)
    if rho < 1e-12:
        return math.degrees(PHI0), math.degrees(LAM0)

    c = 2.0 * math.asin(min(1.0, rho / (2.0 * R_Q)))
    sinc = math.sin(c)
    cosc = math.cos(c)

    sin_beta = cosc * math.sin(BETA0) + (y_hat * sinc * math.cos(BETA0)) / rho
    beta = math.asin(max(-1.0, min(1.0, sin_beta)))
    q = QP * math.sin(beta)

    lam = LAM0 + math.atan2(x_hat * sinc, rho * math.cos(BETA0) * cosc - y_hat * math.sin(BETA0) * sinc)

    # Newton-Raphson to solve phi from q
    phi = math.asin(max(-1.0, min(1.0, q / 2.0)))
    for _ in range(12):
        sinp = math.sin(phi)
        cosp = math.cos(phi)
        den = (1.0 - E2 * sinp**2)
        q_curr = (1.0 - E2) * (sinp / den - (1.0 / (2.0 * E)) * math.log((1.0 - E * sinp) / (1.0 + E * sinp)))
        diff = q_curr - q
        if abs(diff) < 1e-12:
            break
        dq_dphi = 2.0 * (1.0 - E2) * cosp / (den**2)
        phi -= diff / dq_dphi

    return math.degrees(phi), math.degrees(lam)

def extract_cells(zip_path, min_lat, min_lon, max_lat, max_lon, output_path):
    print(f"Extracting GUS 125m census for BBox: [{min_lat}, {min_lon}] to [{max_lat}, {max_lon}]")

    # Calculate EPSG:3035 coordinate boundaries with 250m safety margin
    corners = [
        forward_epsg3035(min_lat, min_lon),
        forward_epsg3035(min_lat, max_lon),
        forward_epsg3035(max_lat, min_lon),
        forward_epsg3035(max_lat, max_lon),
    ]
    min_e = min(c[0] for c in corners) - 250
    max_e = max(c[0] for c in corners) + 250
    min_n = min(c[1] for c in corners) - 250
    max_n = max(c[1] for c in corners) + 250

    print(f"Projected EPSG:3035 range: Easting [{min_e:.0f}..{max_e:.0f}], Northing [{min_n:.0f}..{max_n:.0f}]")

    code_pattern = re.compile(r'^CRS3035RES125mN(\d+)E(\d+)$')
    results = []
    total_pop = 0

    with zipfile.ZipFile(zip_path) as z:
        # Determine which xlsx parts need to be scanned
        for xlsx_name in ['Grid_125_1.xlsx', 'Grid_125_2.xlsx']:
            if xlsx_name not in z.namelist():
                continue
            
            # Quick check if this part covers our Northing range:
            # Grid_125_1 is approx N2953000 to N3224625
            # Grid_125_2 is approx N3224625 to N3556000
            if xlsx_name == 'Grid_125_1.xlsx' and max_n < 2950000:
                continue
            if xlsx_name == 'Grid_125_1.xlsx' and min_n > 3225000:
                continue
            if xlsx_name == 'Grid_125_2.xlsx' and max_n < 3224000:
                continue

            print(f"Reading {xlsx_name}...")
            with zipfile.ZipFile(z.open(xlsx_name)) as xz:
                target_sst = {}
                sst_idx = 0
                with xz.open('xl/sharedStrings.xml') as sst_file:
                    for _, elem in ET.iterparse(sst_file):
                        if elem.tag.endswith('si'):
                            t_el = elem.find('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}t')
                            text = t_el.text if t_el is not None else ''
                            m = code_pattern.match(text)
                            if m:
                                n = int(m.group(1))
                                e = int(m.group(2))
                                if min_n <= n <= max_n and min_e <= e <= max_e:
                                    target_sst[sst_idx] = (n, e)
                            sst_idx += 1
                            elem.clear()

                print(f"Found {len(target_sst)} matching shared strings in {xlsx_name}")
                if not target_sst:
                    continue

                with xz.open('xl/worksheets/sheet1.xml') as sheet_file:
                    for _, elem in ET.iterparse(sheet_file):
                        if elem.tag.endswith('row'):
                            cells_in_row = elem.findall('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}c')
                            if len(cells_in_row) >= 3:
                                b_val = cells_in_row[1].find('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}v')
                                if b_val is not None and b_val.text and b_val.text.isdigit():
                                    idx = int(b_val.text)
                                    if idx in target_sst:
                                        c_val = cells_in_row[2].find('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}v')
                                        pop = int(c_val.text) if c_val is not None and c_val.text else 0
                                        n, e = target_sst[idx]

                                        # Calculate WGS84 corners for this 125m cell
                                        sw_lat, sw_lon = inverse_epsg3035(e, n)
                                        ne_lat, ne_lon = inverse_epsg3035(e + 125.0, n + 125.0)
                                        ctr_lat, ctr_lon = inverse_epsg3035(e + 62.5, n + 62.5)

                                        results.append({
                                            "id": f"125mN{n}E{e}",
                                            "pop": pop,
                                            "minLat": round(sw_lat, 6),
                                            "minLng": round(sw_lon, 6),
                                            "maxLat": round(ne_lat, 6),
                                            "maxLng": round(ne_lon, 6),
                                            "lat": round(ctr_lat, 6),
                                            "lng": round(ctr_lon, 6)
                                        })
                                        total_pop += pop
                            elem.clear()

    print(f"Extracted total of {len(results)} cells with combined census population {total_pop:,}")

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, 'w', encoding='utf-8') as f:
        json.dump(results, f, separators=(',', ':'))

    size_mb = os.path.getsize(output_path) / (1024 * 1024)
    print(f"Saved optimized JSON to {output_path} ({size_mb:.2f} MB)")

def main():
    parser = argparse.ArgumentParser(description="Extract GUS 125m census population cells to JSON.")
    parser.add_argument("--zip", required=True, help="Path to Grid_125_XLS.zip archive")
    parser.add_argument("--output", default="src/PlanSafe.App/wwwroot/data/gus_krakow_125m.json", help="Output JSON path")
    parser.add_argument("--bbox", default="49.95,19.75,50.15,20.15", help="min_lat,min_lon,max_lat,max_lon")
    args = parser.parse_args()

    min_lat, min_lon, max_lat, max_lon = [float(x) for x in args.bbox.split(',')]
    extract_cells(args.zip, min_lat, min_lon, max_lat, max_lon, args.output)

if __name__ == '__main__':
    main()
