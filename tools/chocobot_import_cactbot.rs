use std::collections::{BTreeMap, BTreeSet};
use std::env;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::process::Command;

const DEFAULT_ARCHIVE_URL: &str = "https://github.com/quisquous/cactbot/archive/refs/heads/main.tar.gz";
const DEFAULT_OUTPUT: &str = "src/Chocobot/Assets/cactbot-imported-triggers.json";
const DEFAULT_TIMELINE_OUTPUT: &str = "src/Chocobot/Assets/cactbot-imported-timelines.json";
const DEFAULT_REPORT: &str = "src/Chocobot/Assets/cactbot-import-report.md";
const RAIDBOSS_DATA: &str = "ui/raidboss/data";
const DEFAULT_EXCLUDE: &str = "03-hw/trial/sophia-ex.ts";

#[derive(Default)]
struct Args {
    cactbot_dir: Option<PathBuf>,
    download: bool,
    archive_url: String,
    output: PathBuf,
    timeline_output: PathBuf,
    report: PathBuf,
    exclude_files: Vec<String>,
    help: bool,
}

#[derive(Clone)]
struct Trigger {
    id: String,
    zone: Option<String>,
    event_type: String,
    ids: Vec<String>,
    pattern: String,
    target_self: bool,
    alert: String,
    duration: f64,
    suppress: f64,
    countdown: Option<f64>,
}

#[derive(Clone)]
struct Timeline {
    id: String,
    zone: Option<String>,
    syncs: Vec<TimelineSync>,
    entries: Vec<TimelineEntry>,
    cues: Vec<TimelineCue>,
}

#[derive(Clone)]
struct TimelineSync {
    time: f64,
    pattern: String,
}

#[derive(Clone)]
struct TimelineCue {
    id: String,
    time: f64,
    before: f64,
    alert: String,
    duration: f64,
}

struct ImportResult {
    trigger: Option<Trigger>,
    reason: Option<String>,
    cactbot_id: Option<String>,
    file: String,
}

fn main() {
    if let Err(err) = run() {
        eprintln!("{err}");
        std::process::exit(1);
    }
}

fn run() -> Result<(), String> {
    let args = parse_args()?;
    if args.help {
        print_help();
        return Ok(());
    }

    let temp_root;
    let cactbot_root = if args.download {
        temp_root = download_cactbot(&args.archive_url)?;
        find_download_root(&temp_root)?
    } else {
        args.cactbot_dir
            .clone()
            .ok_or_else(|| "Pass --cactbot-dir or --download.".to_string())?
    };

    let data_dir = cactbot_root.join(RAIDBOSS_DATA);
    if !data_dir.is_dir() {
        return Err(format!("Missing cactbot raidboss data directory: {}", data_dir.display()));
    }
    let zone_names = load_zone_names(&cactbot_root)?;

    let mut files = Vec::new();
    collect_ts_files(&data_dir, &mut files)?;
    files.sort();

    let excludes: Vec<String> = args
        .exclude_files
        .iter()
        .map(|path| normalize_path(Path::new(path)))
        .collect();

    let mut triggers = Vec::new();
    let mut timelines = Vec::new();
    let mut skipped = Vec::new();
    let mut file_stats: BTreeMap<String, BTreeMap<String, usize>> = BTreeMap::new();

    for path in files {
        let rel = normalize_path(path.strip_prefix(&data_dir).unwrap_or(&path));
        if excludes.iter().any(|exclude| rel.ends_with(exclude)) {
            inc_stat(&mut file_stats, &rel, "excluded");
            continue;
        }

        let text = fs::read_to_string(&path)
            .map_err(|err| format!("Failed to read {}: {err}", path.display()))?;
        let zone = extract_zone_name(&text, &zone_names);
        let blocks = extract_trigger_blocks(&text);
        let timeline = convert_timeline(&text, &data_dir, &rel, zone.clone());
        if let Some(timeline) = timeline {
            inc_stat(&mut file_stats, &rel, "timeline imported");
            timelines.push(timeline);
        }

        if blocks.is_empty() {
            inc_stat(&mut file_stats, &rel, "no-trigger-blocks");
            continue;
        }

        for block in blocks {
            let result = convert_trigger_block(&block, &rel, zone.clone());
            match result.trigger {
                Some(trigger) => {
                    triggers.push(trigger);
                    inc_stat(&mut file_stats, &rel, "imported");
                }
                None => {
                    let reason = result.reason.clone().unwrap_or_else(|| "skipped".to_string());
                    inc_stat(&mut file_stats, &rel, &reason);
                    skipped.push(result);
                }
            }
        }
    }

    triggers = dedupe_triggers(triggers);
    write_trigger_json(&args.output, &triggers)?;
    write_timeline_json(&args.timeline_output, &timelines)?;
    write_report(&args, &cactbot_root, &triggers, &timelines, &skipped, &file_stats)?;

    println!("Imported {} triggers into {}", triggers.len(), args.output.display());
    println!("Imported {} timelines into {}", timelines.len(), args.timeline_output.display());
    println!("Wrote import report to {}", args.report.display());
    Ok(())
}

fn parse_args() -> Result<Args, String> {
    let mut args = Args {
        archive_url: DEFAULT_ARCHIVE_URL.to_string(),
        output: PathBuf::from(DEFAULT_OUTPUT),
        timeline_output: PathBuf::from(DEFAULT_TIMELINE_OUTPUT),
        report: PathBuf::from(DEFAULT_REPORT),
        exclude_files: vec![DEFAULT_EXCLUDE.to_string()],
        ..Default::default()
    };

    let mut iter = env::args().skip(1);
    while let Some(arg) = iter.next() {
        match arg.as_str() {
            "-h" | "--help" => args.help = true,
            "--download" => args.download = true,
            "--cactbot-dir" => args.cactbot_dir = Some(next_path(&mut iter, "--cactbot-dir")?),
            "--archive-url" => args.archive_url = next_string(&mut iter, "--archive-url")?,
            "--output" => args.output = next_path(&mut iter, "--output")?,
            "--timeline-output" => args.timeline_output = next_path(&mut iter, "--timeline-output")?,
            "--report" => args.report = next_path(&mut iter, "--report")?,
            "--exclude-file" => args.exclude_files.push(next_string(&mut iter, "--exclude-file")?),
            unknown => return Err(format!("Unknown argument: {unknown}")),
        }
    }

    Ok(args)
}

fn next_string(iter: &mut impl Iterator<Item = String>, name: &str) -> Result<String, String> {
    iter.next().ok_or_else(|| format!("{name} requires a value"))
}

fn next_path(iter: &mut impl Iterator<Item = String>, name: &str) -> Result<PathBuf, String> {
    Ok(PathBuf::from(next_string(iter, name)?))
}

fn print_help() {
    println!(
        "Import simple cactbot raidboss triggers into Chocobot JSON.\n\
\n\
Usage:\n\
  rustc tools/chocobot_import_cactbot.rs -o /tmp/chocobot_import_cactbot\n\
  /tmp/chocobot_import_cactbot --cactbot-dir /path/to/cactbot\n\
  /tmp/chocobot_import_cactbot --download\n\
\n\
Options:\n\
  --cactbot-dir <path>     Path to a local cactbot checkout.\n\
  --download               Download cactbot main into /tmp and import from it.\n\
  --archive-url <url>      cactbot tar.gz archive URL used with --download.\n\
  --output <path>          Generated trigger JSON. Default: {DEFAULT_OUTPUT}\n\
  --timeline-output <path> Generated timeline JSON. Default: {DEFAULT_TIMELINE_OUTPUT}\n\
  --report <path>          Markdown import report. Default: {DEFAULT_REPORT}\n\
  --exclude-file <suffix>  raidboss data file suffix to skip. Repeatable.\n\
  --help                   Show this help.\n\
\n\
This is conservative: it imports static ID-based netRegex triggers and reports\n\
dynamic/stateful/timeline triggers for later Chocobot engine work."
    );
}

fn download_cactbot(archive_url: &str) -> Result<PathBuf, String> {
    let root = env::temp_dir().join(format!("chocobot-cactbot-{}", std::process::id()));
    if root.exists() {
        fs::remove_dir_all(&root)
            .map_err(|err| format!("Failed to clear {}: {err}", root.display()))?;
    }
    fs::create_dir_all(&root).map_err(|err| format!("Failed to create {}: {err}", root.display()))?;

    let archive = root.join("cactbot.tar.gz");
    let curl_status = Command::new("curl")
        .arg("-L")
        .arg("-o")
        .arg(&archive)
        .arg(archive_url)
        .status()
        .map_err(|err| format!("Failed to run curl: {err}"))?;
    if !curl_status.success() {
        return Err(format!("curl failed while downloading {archive_url}"));
    }

    let tar_status = Command::new("tar")
        .arg("-xzf")
        .arg(&archive)
        .arg("-C")
        .arg(&root)
        .status()
        .map_err(|err| format!("Failed to run tar: {err}"))?;
    if !tar_status.success() {
        return Err(format!("tar failed while extracting {}", archive.display()));
    }

    Ok(root)
}

fn find_download_root(temp_root: &Path) -> Result<PathBuf, String> {
    fs::read_dir(temp_root)
        .map_err(|err| format!("Failed to read {}: {err}", temp_root.display()))?
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .find(|path| path.is_dir() && path.join(RAIDBOSS_DATA).is_dir())
        .ok_or_else(|| "Downloaded cactbot archive did not contain a raidboss data directory.".to_string())
}

fn collect_ts_files(dir: &Path, files: &mut Vec<PathBuf>) -> Result<(), String> {
    for entry in fs::read_dir(dir).map_err(|err| format!("Failed to read {}: {err}", dir.display()))? {
        let path = entry
            .map_err(|err| format!("Failed to read entry in {}: {err}", dir.display()))?
            .path();
        if path.is_dir() {
            collect_ts_files(&path, files)?;
        } else if path.extension().and_then(|ext| ext.to_str()) == Some("ts") {
            files.push(path);
        }
    }
    Ok(())
}

fn load_zone_names(cactbot_root: &Path) -> Result<BTreeMap<String, String>, String> {
    let zone_id_path = cactbot_root.join("resources/zone_id.ts");
    let zone_info_path = cactbot_root.join("resources/zone_info.ts");
    let zone_id_text = fs::read_to_string(&zone_id_path)
        .map_err(|err| format!("Failed to read {}: {err}", zone_id_path.display()))?;
    let zone_info_text = fs::read_to_string(&zone_info_path)
        .map_err(|err| format!("Failed to read {}: {err}", zone_info_path.display()))?;

    let mut symbol_to_id = BTreeMap::new();
    for line in zone_id_text.lines() {
        let trimmed = line.trim();
        if !trimmed.starts_with('\'') {
            continue;
        }
        let Some(symbol_end) = trimmed[1..].find('\'') else {
            continue;
        };
        let symbol = &trimmed[1..1 + symbol_end];
        let Some(colon) = trimmed.find(':') else {
            continue;
        };
        let number: String = trimmed[colon + 1..]
            .chars()
            .skip_while(|ch| ch.is_whitespace())
            .take_while(|ch| ch.is_ascii_digit())
            .collect();
        if !number.is_empty() {
            symbol_to_id.insert(symbol.to_string(), number);
        }
    }

    let mut id_to_name = BTreeMap::new();
    let mut current_id: Option<String> = None;
    let mut in_name = false;
    for line in zone_info_text.lines() {
        let trimmed = line.trim();
        if trimmed.ends_with('{') && trimmed.chars().next().is_some_and(|ch| ch.is_ascii_digit()) {
            current_id = trimmed.split(':').next().map(|value| value.trim().to_string());
            in_name = false;
            continue;
        }
        if trimmed.starts_with("'name':") {
            in_name = true;
            continue;
        }
        if in_name && trimmed.starts_with("'en':") {
            if let Some(id) = current_id.clone() {
                if let Some(name) = extract_quoted_after_colon(trimmed) {
                    id_to_name.insert(id, name);
                }
            }
            in_name = false;
        }
    }

    let mut symbol_to_name = BTreeMap::new();
    for (symbol, id) in symbol_to_id {
        if let Some(name) = id_to_name.get(&id) {
            symbol_to_name.insert(symbol, name.clone());
        }
    }
    Ok(symbol_to_name)
}

fn extract_quoted_after_colon(line: &str) -> Option<String> {
    let colon = line.find(':')?;
    let value = line[colon + 1..].trim().trim_end_matches(',');
    quoted_text(value)
}

fn extract_zone_name(text: &str, zone_names: &BTreeMap<String, String>) -> Option<String> {
    let stripped = strip_comments(text);
    let marker = "zoneId:";
    let start = stripped.find(marker)? + marker.len();
    let rest = stripped[start..].trim_start();
    let zone_marker = "ZoneId.";
    let symbol_start = rest.find(zone_marker)? + zone_marker.len();
    let symbol: String = rest[symbol_start..]
        .chars()
        .take_while(|ch| ch.is_ascii_alphanumeric() || *ch == '_')
        .collect();
    zone_names.get(&symbol).cloned()
}

fn extract_trigger_blocks(text: &str) -> Vec<String> {
    let stripped = strip_comments(text);
    let Some(array) = find_named_array(&stripped, "triggers") else {
        return Vec::new();
    };
    split_top_level_objects(&array)
}

fn extract_timeline_trigger_blocks(text: &str) -> Vec<String> {
    let stripped = strip_comments(text);
    let Some(array) = find_named_array(&stripped, "timelineTriggers") else {
        return Vec::new();
    };
    split_top_level_objects(&array)
}

#[derive(Clone)]
struct TimelineEntry {
    time: f64,
    text: String,
    ids: Vec<String>,
}

fn convert_timeline(text: &str, data_dir: &Path, rel: &str, zone: Option<String>) -> Option<Timeline> {
    let timeline_file = extract_string_property(text, "timelineFile")?;
    let timeline_path = data_dir.join(Path::new(rel).parent().unwrap_or_else(|| Path::new(""))).join(timeline_file);
    let timeline_text = fs::read_to_string(timeline_path).ok()?;
    let entries = parse_timeline_entries(&timeline_text);
    if entries.is_empty() {
        return None;
    }

    let mut syncs = Vec::new();
    for entry in &entries {
        if !entry.ids.is_empty() {
            syncs.push(TimelineSync {
                time: entry.time,
                pattern: make_id_pattern(&entry.ids),
            });
        }
    }

    let mut cues = Vec::new();
    for block in extract_timeline_trigger_blocks(text) {
        let Some(cactbot_id) = extract_string_property(&block, "id") else {
            continue;
        };
        let Some(regex) = extract_timeline_regex(&block) else {
            continue;
        };
        let Some(before) = extract_number_property(&block, "beforeSeconds") else {
            continue;
        };
        let Some(alert) = extract_alert_text(&block, false) else {
            continue;
        };
        if alert.contains("${") {
            continue;
        }
        let duration = extract_number_property(&block, "durationSeconds").unwrap_or(5.0);
        for entry in entries.iter().filter(|entry| timeline_regex_matches(&regex, &entry.text)) {
            cues.push(TimelineCue {
                id: format!("timeline-{}-{}-{}", slugify(&cactbot_id), slugify(&entry.text), format_number(entry.time)),
                time: entry.time,
                before,
                alert: alert.clone(),
                duration,
            });
        }
    }

    cues = dedupe_cues(cues);
    if syncs.is_empty() || entries.is_empty() {
        return None;
    }

    Some(Timeline {
        id: format!("cactbot-timeline-{}", slugify(rel.trim_end_matches(".ts"))),
        zone,
        syncs,
        entries,
        cues,
    })
}

fn parse_timeline_entries(text: &str) -> Vec<TimelineEntry> {
    let mut entries = Vec::new();
    for raw_line in text.lines() {
        let line = raw_line.trim();
        if line.is_empty() || line.starts_with('#') || line.starts_with("hideall") {
            continue;
        }
        let mut chars = line.char_indices();
        let mut end_time = 0;
        for (idx, ch) in &mut chars {
            if ch.is_ascii_digit() || ch == '.' {
                end_time = idx + ch.len_utf8();
            } else {
                break;
            }
        }
        if end_time == 0 {
            continue;
        }
        let Ok(time) = line[..end_time].parse::<f64>() else {
            continue;
        };
        let rest = line[end_time..].trim_start();
        if !rest.starts_with('"') {
            continue;
        }
        let Some(end_quote) = rest[1..].find('"') else {
            continue;
        };
        let text = rest[1..1 + end_quote].to_string();
        if text.starts_with("--") {
            continue;
        }
        let details = &rest[2 + end_quote..];
        let ids = extract_ids_from_text(details);
        entries.push(TimelineEntry { time, text, ids });
    }
    entries
}

fn extract_timeline_regex(block: &str) -> Option<String> {
    let value = extract_property_value(block, "regex")?;
    if value.starts_with('/') {
        let end = value.rfind('/')?;
        if end > 0 {
            return Some(value[1..end].to_string());
        }
    }
    quoted_text(&value)
}

fn timeline_regex_matches(regex: &str, text: &str) -> bool {
    let needle = simplify_timeline_pattern(regex);
    let haystack = simplify_timeline_pattern(text);
    !needle.is_empty() && haystack.contains(&needle)
}

fn simplify_timeline_pattern(value: &str) -> String {
    value
        .replace("\\(", "(")
        .replace("\\)", ")")
        .replace("\\/", "/")
        .replace("\\?", "?")
        .replace("\\.", ".")
        .replace("\\b", "")
        .replace('^', "")
        .replace('$', "")
        .to_ascii_lowercase()
}

fn extract_ids_from_text(value: &str) -> Vec<String> {
    let mut ids = BTreeSet::new();
    let chars: Vec<char> = value.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if chars[i].is_ascii_hexdigit() {
            let start = i;
            while i < chars.len() && chars[i].is_ascii_hexdigit() {
                i += 1;
            }
            let len = i - start;
            if (3..=6).contains(&len) {
                ids.insert(chars[start..i].iter().collect::<String>().to_uppercase());
            }
            continue;
        }
        i += 1;
    }
    ids.into_iter().collect()
}

fn strip_comments(text: &str) -> String {
    let chars: Vec<char> = text.chars().collect();
    let mut out = String::with_capacity(text.len());
    let mut i = 0;
    let mut in_string: Option<char> = None;

    while i < chars.len() {
        let ch = chars[i];
        let next = chars.get(i + 1).copied();
        if let Some(quote) = in_string {
            out.push(ch);
            if ch == '\\' {
                if let Some(escaped) = next {
                    out.push(escaped);
                    i += 2;
                    continue;
                }
            } else if ch == quote {
                in_string = None;
            }
            i += 1;
            continue;
        }

        if ch == '\'' || ch == '"' || ch == '`' {
            in_string = Some(ch);
            out.push(ch);
            i += 1;
            continue;
        }
        if ch == '/' && next == Some('/') {
            while i < chars.len() && chars[i] != '\n' {
                i += 1;
            }
            out.push('\n');
            continue;
        }
        if ch == '/' && next == Some('*') {
            i += 2;
            while i + 1 < chars.len() && !(chars[i] == '*' && chars[i + 1] == '/') {
                out.push(if chars[i] == '\n' { '\n' } else { ' ' });
                i += 1;
            }
            i += 2;
            continue;
        }

        out.push(ch);
        i += 1;
    }

    out
}

fn find_named_array(text: &str, name: &str) -> Option<String> {
    let needle = format!("{name}:");
    let mut search_from = 0;
    while let Some(offset) = text[search_from..].find(&needle) {
        let colon = search_from + offset + needle.len();
        let chars: Vec<char> = text[colon..].chars().collect();
        let mut local = 0;
        while local < chars.len() && chars[local].is_whitespace() {
            local += 1;
        }
        if chars.get(local) == Some(&'[') {
            let start = colon + chars[..local].iter().map(|ch| ch.len_utf8()).sum::<usize>();
            let end = find_matching(text, start, '[', ']')?;
            return Some(text[start + 1..end].to_string());
        }
        search_from = colon;
    }
    None
}

fn find_matching(text: &str, start: usize, open: char, close: char) -> Option<usize> {
    let chars: Vec<(usize, char)> = text.char_indices().collect();
    let mut idx = chars.iter().position(|(byte, _)| *byte == start)?;
    let mut depth = 0usize;
    let mut in_string: Option<char> = None;

    while idx < chars.len() {
        let (byte, ch) = chars[idx];
        if let Some(quote) = in_string {
            if ch == '\\' {
                idx += 2;
                continue;
            }
            if ch == quote {
                in_string = None;
            }
            idx += 1;
            continue;
        }
        if ch == '\'' || ch == '"' || ch == '`' {
            in_string = Some(ch);
            idx += 1;
            continue;
        }
        if ch == open {
            depth += 1;
        } else if ch == close {
            depth = depth.saturating_sub(1);
            if depth == 0 {
                return Some(byte);
            }
        }
        idx += 1;
    }
    None
}

fn split_top_level_objects(text: &str) -> Vec<String> {
    let mut blocks = Vec::new();
    let mut search_from = 0;
    while let Some(offset) = text[search_from..].find('{') {
        let start = search_from + offset;
        let Some(end) = find_matching(text, start, '{', '}') else {
            break;
        };
        blocks.push(text[start..=end].to_string());
        search_from = end + 1;
    }
    blocks
}

fn convert_trigger_block(block: &str, file: &str, zone: Option<String>) -> ImportResult {
    let Some(cactbot_id) = extract_string_property(block, "id") else {
        return skipped(file, None, "missing id");
    };
    let Some(event_type) = extract_string_property(block, "type") else {
        return skipped(file, Some(cactbot_id), "missing event type");
    };
    let Some(net_regex) = extract_object_property(block, "netRegex") else {
        return skipped(file, Some(cactbot_id), "missing netRegex");
    };
    let ids = extract_net_regex_ids(&net_regex, &event_type);
    if ids.is_empty() {
        return skipped(file, Some(cactbot_id), "missing static netRegex id");
    }
    let target_self = block.contains("Conditions.targetIsYou");
    let Some(text) = extract_alert_text(block, target_self) else {
        return skipped(file, Some(cactbot_id), "missing static alert text");
    };
    if text.contains("${") {
        return skipped(file, Some(cactbot_id), "dynamic alert text");
    }

    let duration = extract_number_property(block, "durationSeconds").unwrap_or_else(|| default_duration(block));
    let suppress = extract_number_property(block, "suppressSeconds").unwrap_or_else(|| default_suppress(&event_type));
    let countdown = extract_number_property(block, "delaySeconds").filter(|value| *value > 0.0);
    let trigger = Trigger {
        id: format!("cactbot-{}", slugify(&cactbot_id)),
        zone,
        event_type,
        ids: ids.clone(),
        pattern: make_id_pattern(&ids),
        target_self,
        alert: text,
        duration,
        suppress,
        countdown,
    };
    ImportResult {
        trigger: Some(trigger),
        reason: None,
        cactbot_id: Some(cactbot_id),
        file: file.to_string(),
    }
}

fn skipped(file: &str, cactbot_id: Option<String>, reason: &str) -> ImportResult {
    ImportResult {
        trigger: None,
        reason: Some(reason.to_string()),
        cactbot_id,
        file: file.to_string(),
    }
}

fn extract_string_property(block: &str, name: &str) -> Option<String> {
    let value = extract_property_value(block, name)?;
    let mut chars = value.chars();
    let quote = chars.next()?;
    if quote != '\'' && quote != '"' {
        return None;
    }
    let end = value.rfind(quote)?;
    if end == 0 {
        return None;
    }
    Some(unescape_ts_string(&value[1..end]))
}

fn extract_object_property(block: &str, name: &str) -> Option<String> {
    let pattern = format!("{name}:");
    let start_name = block.find(&pattern)?;
    let after_colon = start_name + pattern.len();
    let brace_offset = block[after_colon..].find('{')?;
    let start = after_colon + brace_offset;
    let end = find_matching(block, start, '{', '}')?;
    Some(block[start..=end].to_string())
}

fn extract_property_value(block: &str, name: &str) -> Option<String> {
    let pattern = format!("{name}:");
    let start = block.find(&pattern)? + pattern.len();
    let bytes = block.as_bytes();
    let mut i = start;
    while i < bytes.len() && bytes[i].is_ascii_whitespace() {
        i += 1;
    }
    if i >= bytes.len() {
        return None;
    }
    let ch = block[i..].chars().next()?;
    if ch == '\'' || ch == '"' {
        let quote = ch;
        let mut end = i + ch.len_utf8();
        while end < block.len() {
            let current = block[end..].chars().next()?;
            if current == '\\' {
                end += current.len_utf8();
                if end < block.len() {
                    end += block[end..].chars().next()?.len_utf8();
                }
                continue;
            }
            if current == quote {
                return Some(block[i..end + current.len_utf8()].to_string());
            }
            end += current.len_utf8();
        }
        return None;
    }
    if ch == '[' {
        let end = find_matching(block, i, '[', ']')?;
        return Some(block[i..=end].to_string());
    }
    if ch == '{' {
        let end = find_matching(block, i, '{', '}')?;
        return Some(block[i..=end].to_string());
    }
    if ch == '/' {
        let mut end = i + 1;
        let mut escaped = false;
        while end < block.len() {
            let current = block[end..].chars().next()?;
            if escaped {
                escaped = false;
            } else if current == '\\' {
                escaped = true;
            } else if current == '/' {
                return Some(block[i..end + 1].to_string());
            }
            end += current.len_utf8();
        }
        return None;
    }

    let mut end = i;
    while end < block.len() {
        let current = block[end..].chars().next()?;
        if current == ',' || current == '}' || current == '\n' {
            break;
        }
        end += current.len_utf8();
    }
    Some(block[i..end].trim().to_string())
}

fn extract_number_property(block: &str, name: &str) -> Option<f64> {
    let value = extract_property_value(block, name)?;
    let numeric = value.trim();
    if numeric.chars().all(|ch| ch.is_ascii_digit() || ch == '.' || ch == '-') {
        numeric.parse().ok()
    } else {
        None
    }
}

fn extract_net_regex_ids(net_regex: &str, event_type: &str) -> Vec<String> {
    let value = extract_property_value(net_regex, "id")
        .or_else(|| {
            if matches!(event_type, "GainsEffect" | "LosesEffect") {
                extract_property_value(net_regex, "effectId")
            } else {
                None
            }
        });
    let Some(value) = value else {
        return Vec::new();
    };
    let mut ids = BTreeSet::new();
    let chars: Vec<char> = value.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if chars[i].is_ascii_hexdigit() {
            let start = i;
            while i < chars.len() && chars[i].is_ascii_hexdigit() {
                i += 1;
            }
            let len = i - start;
            if (3..=6).contains(&len) {
                ids.insert(chars[start..i].iter().collect::<String>().to_uppercase());
            }
            continue;
        }
        i += 1;
    }
    ids.into_iter().collect()
}

fn extract_alert_text(block: &str, allow_target_self_dynamic_branch: bool) -> Option<String> {
    for field in ["alarmText", "alertText", "infoText"] {
        if let Some(text) = extract_static_text_field(block, field, allow_target_self_dynamic_branch) {
            return Some(text);
        }
    }
    let response = extract_response_name(block)?;
    response_text(&response).map(|text| text.to_string())
}

fn extract_static_text_field(block: &str, field: &str, allow_target_self_dynamic_branch: bool) -> Option<String> {
    let value = extract_property_value(block, field)?;
    if let Some(text) = quoted_text(&value) {
        return Some(clean_text(&text));
    }
    if let Some(text) = extract_english_from_object(&value) {
        return Some(text);
    }
    let output_strings = extract_object_property(block, "outputStrings")?;
    if let Some(output_key) = extract_output_key(&value) {
        return extract_output_string(&output_strings, &output_key);
    }

    let output_key = extract_output_key_after_field(block, field, allow_target_self_dynamic_branch)?;
    extract_output_string(&output_strings, &output_key)
}

fn quoted_text(value: &str) -> Option<String> {
    let first = value.chars().next()?;
    if first != '\'' && first != '"' {
        return None;
    }
    let end = value.rfind(first)?;
    if end == 0 {
        return None;
    }
    Some(unescape_ts_string(&value[1..end]))
}

fn extract_english_from_object(value: &str) -> Option<String> {
    let raw = extract_property_value(value, "en")?;
    quoted_text(&raw).map(|text| clean_text(&text))
}

fn extract_output_key(value: &str) -> Option<String> {
    let marker = "output.";
    let start = value.find(marker)? + marker.len();
    let rest = &value[start..];
    let key: String = rest
        .chars()
        .take_while(|ch| ch.is_ascii_alphanumeric() || *ch == '_')
        .collect();
    if key.is_empty() {
        None
    } else {
        Some(key)
    }
}

fn extract_output_key_after_field(block: &str, field: &str, allow_dynamic_branch: bool) -> Option<String> {
    let marker = format!("{field}:");
    let start = block.find(&marker)? + marker.len();
    let tail = &block[start..];
    let field_body = tail.split("outputStrings").next().unwrap_or(tail);
    if allow_dynamic_branch {
        if let Some(output_key) = extract_last_returned_output_key(field_body) {
            return Some(output_key);
        }
    }
    if field_body.contains("data.") || field_body.contains("matches.") || block.contains("Conditions.targetIsYou") {
        return None;
    }
    extract_output_key(field_body)
}

fn extract_last_returned_output_key(field_body: &str) -> Option<String> {
    let marker = "return output.";
    let mut key = None;
    let mut search_from = 0;
    while let Some(offset) = field_body[search_from..].find(marker) {
        let start = search_from + offset + marker.len();
        let rest = &field_body[start..];
        let candidate: String = rest
            .chars()
            .take_while(|ch| ch.is_ascii_alphanumeric() || *ch == '_')
            .collect();
        if !candidate.is_empty() {
            key = Some(candidate);
        }
        search_from = start + rest.chars().next().map(char::len_utf8).unwrap_or(1);
    }
    key
}

fn extract_output_string(output_strings: &str, key: &str) -> Option<String> {
    let object = extract_object_property(output_strings, key)?;
    extract_english_from_object(&object)
}

fn extract_response_name(block: &str) -> Option<String> {
    let marker = "response:";
    let start = block.find(marker)? + marker.len();
    let after = &block[start..];
    let responses = "Responses.";
    let response_start = after.find(responses)? + responses.len();
    let rest = &after[response_start..];
    let name: String = rest
        .chars()
        .take_while(|ch| ch.is_ascii_alphanumeric() || *ch == '_')
        .collect();
    if name.is_empty() {
        None
    } else {
        Some(name)
    }
}

fn response_text(name: &str) -> Option<&'static str> {
    Some(match name {
        "aoe" => "Raidwide AoE",
        "awayFromFront" => "Avoid front",
        "bigAoe" => "Big raidwide AoE",
        "bleedAoe" => "Raidwide bleed",
        "breakChains" => "Break chains",
        "cleanse" => "Cleanse",
        "defamation" => "Move away",
        "doritoStack" => "Stack on marker",
        "drawIn" => "Draw-in",
        "earthshaker" => "Bait away",
        "getBehind" => "Get behind",
        "getIn" => "Get in",
        "getOut" => "Get out",
        "getUnder" => "Get under",
        "interrupt" => "Interrupt",
        "knockback" => "Knockback",
        "lookAway" => "Look away",
        "lookAwayFromSource" => "Look away",
        "moveAway" => "Move away",
        "moveAwayFromFront" => "Avoid front",
        "moveBehind" => "Get behind",
        "moveIn" => "Get in",
        "moveOut" => "Get out",
        "outOfMelee" => "Out of melee",
        "partnerStack" => "Stack with partner",
        "preyOn" => "Prey",
        "spread" => "Spread",
        "spreadThenStack" => "Spread then stack",
        "stackMarker" => "Stack",
        "stackThenSpread" => "Stack then spread",
        "stopMoving" => "Stop moving",
        "tankBuster" => "Tank buster",
        "tankBusterSwap" => "Tank buster: swap",
        "tankCleave" => "Tank cleave",
        "tankCleaveOn" => "Tank cleave",
        "tankLaser" => "Tank laser",
        "tankLaserOn" => "Tank laser",
        _ => return None,
    })
}

fn make_id_pattern(ids: &[String]) -> String {
    if ids.len() == 1 {
        format!("\\b{}\\b", ids[0])
    } else {
        format!("\\b(?:{})\\b", ids.join("|"))
    }
}

fn default_duration(block: &str) -> f64 {
    if block.contains("alarmText:") {
        6.0
    } else {
        5.0
    }
}

fn default_suppress(event_type: &str) -> f64 {
    match event_type {
        "StartsUsing" | "Ability" => 8.0,
        _ => 5.0,
    }
}

fn slugify(value: &str) -> String {
    let mut slug = String::new();
    let mut previous_dash = false;
    for ch in value.chars() {
        if ch.is_ascii_alphanumeric() {
            slug.push(ch.to_ascii_lowercase());
            previous_dash = false;
        } else if !previous_dash && !slug.is_empty() {
            slug.push('-');
            previous_dash = true;
        }
    }
    while slug.ends_with('-') {
        slug.pop();
    }
    if slug.is_empty() {
        "trigger".to_string()
    } else {
        slug
    }
}

fn clean_text(value: &str) -> String {
    value.split_whitespace().collect::<Vec<_>>().join(" ")
}

fn unescape_ts_string(value: &str) -> String {
    value
        .replace("\\'", "'")
        .replace("\\\"", "\"")
        .replace("\\n", " ")
        .replace("\\`", "`")
}

fn dedupe_triggers(triggers: Vec<Trigger>) -> Vec<Trigger> {
    let mut seen = BTreeSet::new();
    let mut deduped = Vec::new();
    for trigger in triggers {
        let key = format!(
            "{}:{}:{}:{}:{}:{}",
            trigger.zone.as_deref().unwrap_or_default(),
            trigger.event_type,
            trigger.ids.join(","),
            trigger.pattern,
            trigger.target_self,
            trigger.alert
        );
        if seen.insert(key) {
            deduped.push(trigger);
        }
    }
    deduped
}

fn dedupe_cues(cues: Vec<TimelineCue>) -> Vec<TimelineCue> {
    let mut seen = BTreeSet::new();
    let mut deduped = Vec::new();
    for cue in cues {
        let key = format!("{}:{}", cue.id, format_number(cue.time));
        if seen.insert(key) {
            deduped.push(cue);
        }
    }
    deduped
}

fn write_trigger_json(path: &Path, triggers: &[Trigger]) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|err| format!("Failed to create {}: {err}", parent.display()))?;
    }
    let mut out = String::from("[\n");
    for (idx, trigger) in triggers.iter().enumerate() {
        out.push_str("  {\n");
        out.push_str(&format!("    \"id\": \"{}\",\n", json_escape(&trigger.id)));
        if let Some(zone) = &trigger.zone {
            out.push_str(&format!("    \"zone\": \"{}\",\n", json_escape(zone)));
        }
        out.push_str("    \"source\": \"LogLine\",\n");
        out.push_str(&format!("    \"eventType\": \"{}\",\n", json_escape(&trigger.event_type)));
        out.push_str("    \"ids\": [");
        for (id_idx, id) in trigger.ids.iter().enumerate() {
            if id_idx > 0 {
                out.push_str(", ");
            }
            out.push_str(&format!("\"{}\"", json_escape(id)));
        }
        out.push_str("],\n");
        out.push_str(&format!("    \"pattern\": \"{}\",\n", json_escape(&trigger.pattern)));
        if trigger.target_self {
            out.push_str("    \"targetSelf\": true,\n");
        }
        out.push_str(&format!("    \"alert\": \"{}\",\n", json_escape(&trigger.alert)));
        out.push_str(&format!("    \"duration\": {},\n", format_number(trigger.duration)));
        if let Some(countdown) = trigger.countdown {
            out.push_str(&format!("    \"countdown\": {},\n", format_number(countdown)));
        }
        out.push_str(&format!("    \"suppress\": {}\n", format_number(trigger.suppress)));
        out.push_str("  }");
        if idx + 1 != triggers.len() {
            out.push(',');
        }
        out.push('\n');
    }
    out.push_str("]\n");
    fs::write(path, out).map_err(|err| format!("Failed to write {}: {err}", path.display()))
}

fn write_timeline_json(path: &Path, timelines: &[Timeline]) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|err| format!("Failed to create {}: {err}", parent.display()))?;
    }
    let mut out = String::from("[\n");
    for (idx, timeline) in timelines.iter().enumerate() {
        out.push_str("  {\n");
        out.push_str(&format!("    \"id\": \"{}\",\n", json_escape(&timeline.id)));
        if let Some(zone) = &timeline.zone {
            out.push_str(&format!("    \"zone\": \"{}\",\n", json_escape(zone)));
        }
        out.push_str("    \"syncs\": [\n");
        for (sync_idx, sync) in timeline.syncs.iter().enumerate() {
            out.push_str("      {\n");
            out.push_str(&format!("        \"time\": {},\n", format_number(sync.time)));
            out.push_str(&format!("        \"pattern\": \"{}\"\n", json_escape(&sync.pattern)));
            out.push_str("      }");
            if sync_idx + 1 != timeline.syncs.len() {
                out.push(',');
            }
            out.push('\n');
        }
        out.push_str("    ],\n");
        out.push_str("    \"entries\": [\n");
        for (entry_idx, entry) in timeline.entries.iter().enumerate() {
            out.push_str("      {\n");
            out.push_str(&format!("        \"time\": {},\n", format_number(entry.time)));
            out.push_str(&format!("        \"text\": \"{}\"\n", json_escape(&entry.text)));
            out.push_str("      }");
            if entry_idx + 1 != timeline.entries.len() {
                out.push(',');
            }
            out.push('\n');
        }
        out.push_str("    ],\n");
        out.push_str("    \"cues\": [\n");
        for (cue_idx, cue) in timeline.cues.iter().enumerate() {
            out.push_str("      {\n");
            out.push_str(&format!("        \"id\": \"{}\",\n", json_escape(&cue.id)));
            out.push_str(&format!("        \"time\": {},\n", format_number(cue.time)));
            out.push_str(&format!("        \"before\": {},\n", format_number(cue.before)));
            out.push_str(&format!("        \"alert\": \"{}\",\n", json_escape(&cue.alert)));
            out.push_str(&format!("        \"duration\": {}\n", format_number(cue.duration)));
            out.push_str("      }");
            if cue_idx + 1 != timeline.cues.len() {
                out.push(',');
            }
            out.push('\n');
        }
        out.push_str("    ]\n");
        out.push_str("  }");
        if idx + 1 != timelines.len() {
            out.push(',');
        }
        out.push('\n');
    }
    out.push_str("]\n");
    fs::write(path, out).map_err(|err| format!("Failed to write {}: {err}", path.display()))
}

fn write_report(
    args: &Args,
    cactbot_root: &Path,
    triggers: &[Trigger],
    timelines: &[Timeline],
    skipped: &[ImportResult],
    file_stats: &BTreeMap<String, BTreeMap<String, usize>>,
) -> Result<(), String> {
    if let Some(parent) = args.report.parent() {
        fs::create_dir_all(parent).map_err(|err| format!("Failed to create {}: {err}", parent.display()))?;
    }

    let mut reason_counts: BTreeMap<String, usize> = BTreeMap::new();
    for result in skipped {
        *reason_counts
            .entry(result.reason.clone().unwrap_or_else(|| "unknown".to_string()))
            .or_insert(0) += 1;
    }
    let imported_files = file_stats
        .values()
        .filter(|stats| stats.get("imported").copied().unwrap_or(0) > 0)
        .count();

    let mut out = String::new();
    out.push_str("# Chocobot cactbot import report\n\n");
    out.push_str(&format!("- cactbot source: `{}`\n", cactbot_root.display()));
    out.push_str(&format!("- output: `{}`\n", args.output.display()));
    out.push_str(&format!("- timeline output: `{}`\n", args.timeline_output.display()));
    out.push_str(&format!("- imported triggers: {}\n", triggers.len()));
    out.push_str(&format!("- imported timelines: {}\n", timelines.len()));
    out.push_str(&format!("- files with imports: {} / {}\n", imported_files, file_stats.len()));
    out.push_str(&format!("- skipped trigger objects: {}\n\n", skipped.len()));

    out.push_str("## Skipped Reasons\n\n");
    let mut counts: Vec<_> = reason_counts.into_iter().collect();
    counts.sort_by(|a, b| b.1.cmp(&a.1).then_with(|| a.0.cmp(&b.0)));
    for (reason, count) in counts {
        out.push_str(&format!("- {reason}: {count}\n"));
    }

    out.push_str("\n## File Summary\n\n");
    for (file, stats) in file_stats {
        let imported = stats.get("imported").copied().unwrap_or(0);
        let excluded = stats.get("excluded").copied().unwrap_or(0);
        let skipped_count: usize = stats
            .iter()
            .filter(|(key, _)| key.as_str() != "imported" && key.as_str() != "excluded")
            .map(|(_, value)| *value)
            .sum();
        if imported == 0 && skipped_count == 0 && excluded == 0 {
            continue;
        }
        let mut parts = Vec::new();
        if imported > 0 {
            parts.push(format!("imported {imported}"));
        }
        if skipped_count > 0 {
            parts.push(format!("skipped {skipped_count}"));
        }
        if excluded > 0 {
            parts.push("excluded".to_string());
        }
        out.push_str(&format!("- `{file}`: {}\n", parts.join(", ")));
    }

    out.push_str("\n## Skipped Trigger Details\n\n");
    for result in skipped.iter().take(1000) {
        out.push_str(&format!(
            "- `{}` `{}`: {}\n",
            result.file,
            result.cactbot_id.as_deref().unwrap_or("unknown"),
            result.reason.as_deref().unwrap_or("unknown")
        ));
    }
    if skipped.len() > 1000 {
        out.push_str(&format!(
            "- ... {} more skipped triggers omitted from this report\n",
            skipped.len() - 1000
        ));
    }

    out.push_str(
        "\n## Notes\n\n\
- Imported triggers are zone-scoped when cactbot's ZoneId maps to an English zone name.\n\
- Imported triggers include structured event type and ID metadata, with raw regex patterns retained as a fallback.\n\
- `Conditions.targetIsYou()` imports as a `targetSelf` runtime check when a static fallback callout can be derived.\n\
- Imported timelines are conservative: cues are generated from static timelineTriggers and sync from observed ability IDs.\n\
- Dynamic output text, role checks, state collectors, and geometry solvers are otherwise intentionally skipped.\n\
- Re-run this importer after updating cactbot to identify newly importable or newly skipped encounters.\n",
    );

    fs::write(&args.report, out).map_err(|err| format!("Failed to write {}: {err}", args.report.display()))
}

fn inc_stat(stats: &mut BTreeMap<String, BTreeMap<String, usize>>, file: &str, key: &str) {
    *stats
        .entry(file.to_string())
        .or_default()
        .entry(key.to_string())
        .or_insert(0) += 1;
}

fn normalize_path(path: &Path) -> String {
    path.to_string_lossy().replace('\\', "/")
}

fn json_escape(value: &str) -> String {
    let mut escaped = String::new();
    for ch in value.chars() {
        match ch {
            '\\' => escaped.push_str("\\\\"),
            '"' => escaped.push_str("\\\""),
            '\n' => escaped.push_str("\\n"),
            '\r' => escaped.push_str("\\r"),
            '\t' => escaped.push_str("\\t"),
            _ => escaped.push(ch),
        }
    }
    escaped
}

fn format_number(value: f64) -> String {
    if (value.fract()).abs() < f64::EPSILON {
        format!("{}", value as i64)
    } else {
        format!("{value}")
    }
}

impl From<io::Error> for ImportResult {
    fn from(_: io::Error) -> Self {
        ImportResult {
            trigger: None,
            reason: Some("io error".to_string()),
            cactbot_id: None,
            file: String::new(),
        }
    }
}
