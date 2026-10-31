#!/usr/bin/env python3
"""Require the shared layout on card rails, without treating wrapping grids as rails.

Only a control's own ItemsPanel / horizontal scrolling is inspected, not horizontal
panels in its card template/header. Follow BasedOn so page-specific Skyline styles
work. Non-card lists need an immediately preceding `rail-layout: non-card — reason`
comment. Code-built lists must be declared in XAML to use the style safely under AOT;
non-card code-built lists can opt out with the same marker in a // comment.

An optional source-directory argument lets this check run on temporary bad samples.
Uses only Python's standard library; no Windows/XAML build is required.
"""

import argparse
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

SHARED_STYLE = "SkylineCardRailStyle"
XAML = "{http://schemas.microsoft.com/winfx/2006/xaml}"
CONTROLS = {"ListView", "GridView", "ItemsControl"}
OPT_OUT = re.compile(r"rail-layout:\s*non-card\s*[—:-]\s*\S")


def tag(element):
    return element.tag.rsplit("}", 1)[-1] if isinstance(element.tag, str) else ""


def property_name(name):
    return name.rsplit("}", 1)[-1].rsplit(":", 1)[-1]


def resource_key(value):
    match = re.fullmatch(r"\{(?:StaticResource|ThemeResource)\s+(\w+)\}", value)
    return match[1] if match else None


def properties(element):
    if tag(element) == "Style":
        return {property_name(child.get("Property", "")): child.get("Value", "")
                for child in element if tag(child) == "Setter"}
    return {property_name(key): value for key, value in element.attrib.items()}


def horizontal_panel(element):
    if tag(element) == "Style":
        panels = [child for child in element
                  if tag(child) == "Setter" and child.get("Property") == "ItemsPanel"]
    else:
        panels = [child for child in element if tag(child).endswith(".ItemsPanel")]
    # ItemsWrapGrid Orientation=Horizontal wraps into a VERTICAL grid, not a rail.
    return any(tag(child) in {"ItemsStackPanel", "StackPanel"}
               and child.get("Orientation") == "Horizontal"
               for panel in panels for child in panel.iter())


def check(source):
    errors = []
    documents = []
    for path in sorted(source.rglob("*.xaml")):
        if {"bin", "obj"}.intersection(path.parts):
            continue
        try:
            parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
            documents.append((path, ET.parse(path, parser=parser).getroot()))
        except ET.ParseError as error:
            errors.append(f"{path}: invalid XML: {error}")

    styles = {element.get(XAML + "Key"): element for _, root in documents
              for element in root.iter() if tag(element) == "Style" and element.get(XAML + "Key")}

    def style_chain(element):
        if tag(element) != "Style":
            inline = next((child for child in element if tag(child).endswith(".Style")), None)
            element = next(iter(inline), None) if inline is not None else styles.get(resource_key(element.get("Style", "")))
        chain = []
        while element is not None and element not in chain:
            chain.append(element)
            element = styles.get(resource_key(element.get("BasedOn", "")))
        return chain

    if not documents:
        errors.append(f"{source}: no XAML files found")
    shared = styles.get(SHARED_STYLE)
    if shared is None or properties(shared).get("RailLayout.IsEnabled") != "True":
        errors.append(f"{source}: {SHARED_STYLE} must enable RailLayout.IsEnabled")

    rails = non_cards = 0
    for path, root in documents:
        previous = {child: prior for parent in root.iter()
                    for prior, child in zip(list(parent), list(parent)[1:])}
        for element in root.iter():
            is_style = tag(element) == "Style" and element.get("TargetType") in CONTROLS
            if tag(element) not in CONTROLS and not is_style:
                continue
            prior = previous.get(element)
            if prior is not None and prior.tag is ET.Comment and OPT_OUT.search(prior.text or ""):
                non_cards += not is_style
                continue
            chain = style_chain(element)
            nodes = [element] + chain if not is_style else chain
            settings = {}
            for node in reversed(nodes):
                settings.update(properties(node))
            uses_shared = shared is not None and shared in chain
            horizontal = any(horizontal_panel(node) for node in nodes) or settings.get("ScrollViewer.HorizontalScrollMode") == "Enabled"
            if not horizontal and not uses_shared:
                continue
            name = element.get(XAML + "Name") or element.get(XAML + "Key") or "unnamed"
            location = f"{path}: {tag(element)} '{name}'"
            if not uses_shared:
                errors.append(f"{location}: horizontal list must use {SHARED_STYLE} (or explicitly opt out as non-card)")
                continue
            rails += not is_style
            if not is_style:
                for dimension in ("CardWidth", "CardHeight"):
                    if settings.get("RailLayout." + dimension, "0") in {"", "0"}:
                        errors.append(f"{location}: supply RailLayout.{dimension} for the focus envelope")
            own = properties(element)
            if any(key in own for key in ("Padding", "Margin", "Template")) or any(
                    tag(child).endswith((".Padding", ".Margin", ".Template")) for child in element):
                errors.append(f"{location}: rail padding/margins/template belong to the shared layout, not the page/style")
            if not is_style and "RailLayout.ContentInset" in own:
                errors.append(f"{location}: resting insets belong in a derived Skyline style")
            # A local items panel must not reintroduce padding outside the scroll viewport.
            for panel in element:
                if tag(panel).endswith(".ItemsPanel") and any(
                        "Margin" in child.attrib or "Padding" in child.attrib for child in panel.iter()):
                    errors.append(f"{location}: do not set headroom on the items panel")

    # There are currently no code-built card lists. Require XAML rather than resource
    # lookups in code (which crash under Native AOT). Also covers future Views/*.cs lists.
    constructor = re.compile(r"\bnew\s+(?:Windows\.UI\.Xaml\.Controls\.)?(ListView|GridView|ItemsControl)\s*(?=[({;])")
    for path in sorted(source.rglob("*.cs")):
        if {"bin", "obj"}.intersection(path.parts):
            continue
        text = path.read_text(encoding="utf-8")
        code = re.sub(r"//[^\n]*|/\*.*?\*/", lambda match: re.sub(r"[^\n]", " ", match[0]), text, flags=re.S)
        for match in constructor.finditer(code):
            line = text.count("\n", 0, match.start()) + 1
            context = text.splitlines()[max(0, line - 2):line]
            if not any(OPT_OUT.search(comment) for comment in context if "//" in comment):
                errors.append(f"{path}:{line}: declare {match[1]} in XAML with {SHARED_STYLE}, or mark the non-card list")
    return errors, rails, non_cards


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", nargs="?", type=Path, default=Path(__file__).resolve().parents[1] / "src/Bale.Xbox")
    args = parser.parse_args()
    errors, rails, non_cards = check(args.source)
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print(f"Rail layout check passed ({rails} card rails; {non_cards} explicit non-card lists).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
