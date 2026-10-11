#!/usr/bin/env node
// ============================================================================
//  DeepSeek Harness - auto-configurator (Node 18+)
//  Runs before every launch. It:
//    1. probes well-known local model servers (Ollama / LM Studio / llama.cpp
//       / vLLM / LocalAI / Jan / KoboldCpp / oobabooga) plus any extra
//       endpoints from DSH_EXTRA_ENDPOINTS,
//    2. fetches each server's model list,
//    3. merges the detected providers into $DSH_HOME/settings.yaml
//       (user-added providers and other settings sections are preserved, and
//       an existing provider is never deleted when its probe fails),
//    4. writes placeholder credential refs for the endpoints it detected into
//       $DSH_HOME/.credentials.yaml.
//
//  The DeepSeek API key is NEVER persisted: the C# launcher keeps it
//  DPAPI-encrypted and injects DEEPSEEK_API_KEY into the environment before
//  dsh starts, and the credentials provider reads the environment first. The
//  provider entry only carries the `apiKeyEnv: DEEPSEEK_API_KEY` reference.
//
//  No third-party dependencies: the YAML we touch is written/merged with
//  simple line-based rules that match the shape this script emits. Every
//  section this script does not own — including the whole `records:` blob in
//  .credentials.yaml — is copied through verbatim, byte for byte.
// ============================================================================

import fs from 'node:fs';
import path from 'node:path';

const DSH_HOME    = process.env.DSH_HOME || path.join(process.cwd(), 'home');
const SETTINGS    = path.join(DSH_HOME, 'settings.yaml');
const CREDENTIALS = path.join(DSH_HOME, '.credentials.yaml');
// dsh >= 0.2 moved live plugin config out of $DSH_HOME/settings.yaml: the file
// is imported once into the active profile and renamed `settings.yaml.imported`,
// and everything afterwards is persisted per profile entry in the profile's own
// cordis.patch.yml. `llm-pi-ai` was never one of the importable sections, so a
// settings.yaml written after that import is ignored outright. providers and the
// model fallback therefore go into the profile patch now.
const PROFILE     = process.env.DSH_PROFILE || 'web';
const PROFILE_DIR = path.join(DSH_HOME, 'profiles', PROFILE);
const PATCH       = path.join(PROFILE_DIR, 'cordis.patch.yml');

const TIMEOUT_MS  = 2500;
const MAX_MODELS  = 200;

// Well-known local backends (id must start with "auto-" so merges can tell
// generated providers apart from user-managed ones).
const BACKENDS = [
  { id: 'auto-ollama',    name: 'Ollama',     base: 'http://127.0.0.1:11434', v1: '/v1' },
  { id: 'auto-lmstudio',  name: 'LM Studio',  base: 'http://127.0.0.1:1234',  v1: '/v1' },
  { id: 'auto-llamacpp',  name: 'llama.cpp',  base: 'http://127.0.0.1:8080',  v1: '/v1' },
  { id: 'auto-localai',   name: 'LocalAI',    base: 'http://127.0.0.1:8080',  v1: '/v1' },
  { id: 'auto-vllm',      name: 'vLLM',       base: 'http://127.0.0.1:8000',  v1: '/v1' },
  { id: 'auto-jan',       name: 'Jan',        base: 'http://127.0.0.1:1337',  v1: '/v1' },
  { id: 'auto-koboldcpp', name: 'KoboldCpp',  base: 'http://127.0.0.1:5001',  v1: '/v1' },
  { id: 'auto-oobabooga', name: 'oobabooga',  base: 'http://127.0.0.1:5000',  v1: '/v1' },
];

// Extra endpoints: "label|http://host:port|/v1" separated by ";"
function extraBackends() {
  const raw = (process.env.DSH_EXTRA_ENDPOINTS || '').trim();
  if (!raw) return [];
  return raw.split(';')
    .map((s, i) => {
      const parts = s.split('|').map((p) => p.trim());
      return {
        id: `auto-extra${i + 1}`,
        name: parts[0] || `extra${i + 1}`,
        base: parts[1] || '',
        v1: parts[2] || '/v1',
      };
    })
    .filter((b) => b.base);
}

function yamlStr(s) {
  // Always quote: safe for ids like "qwen2.5:7b" and URLs.
  // Must escape backslashes (Windows paths) AND double quotes for valid YAML.
  return `"${String(s).replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
}

// ---- durable writes ------------------------------------------------------

// Replace `target` without ever leaving a half-written document behind: the
// bytes go to a sibling temp file first and are then renamed over the target
// (atomic on NTFS and on POSIX). The previous contents are copied to
// `<target>.bak` first, so a bad run is always recoverable.
function writeFileAtomic(target, text, mode) {
  if (fs.existsSync(target)) {
    try {
      fs.copyFileSync(target, `${target}.bak`);
    } catch (e) {
      console.log(`  (could not refresh ${path.basename(target)}.bak: ${e.message})`);
    }
  }
  const tmp = `${target}.tmp-${process.pid}-${Date.now()}`;
  try {
    fs.writeFileSync(tmp, text, mode === undefined ? 'utf8' : { encoding: 'utf8', mode });
    fs.renameSync(tmp, target);
  } catch (e) {
    try { fs.unlinkSync(tmp); } catch {}
    throw e;
  }
}

async function getJSON(url) {
  const ctrl = new AbortController();
  const t = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
  try {
    const res = await fetch(url, { signal: ctrl.signal, headers: { accept: 'application/json' } });
    if (!res.ok) return null;
    return await res.json();
  } catch {
    return null;
  } finally {
    clearTimeout(t);
  }
}

async function fetchModels(base, v1) {
  let ids = [];
  const json = await getJSON(`${base}${v1}/models`);
  if (json) {
    if (Array.isArray(json.data)) ids = json.data.map((m) => m.id).filter(Boolean);
    else if (Array.isArray(json.models)) ids = json.models.map((m) => m.id ?? m.name).filter(Boolean);
  }
  if (!ids.length) {
    // Ollama native endpoint fallback: /api/tags
    const tags = await getJSON(`${base}/api/tags`);
    if (tags && Array.isArray(tags.models)) ids = tags.models.map((m) => m.name).filter(Boolean);
  }
  ids = [...new Set(ids)]
    .filter((id) => !/(embed|rerank|bge-|nomic-embed)/i.test(id))
    .slice(0, MAX_MODELS);
  return ids.length ? ids : null;
}

async function detect() {
  const found = [];
  const seen = new Set();
  for (const b of [...BACKENDS, ...extraBackends()]) {
    const key = `${b.base}${b.v1}`;
    if (seen.has(key)) continue; // llama.cpp and LocalAI share 8080 -> first wins
    seen.add(key);
    process.stdout.write(`  probing ${String(b.name).padEnd(10)} ${b.base}${b.v1} ... `);
    const models = await fetchModels(b.base, b.v1);
    if (models) {
      found.push({ ...b, models });
      console.log(`OK (${models.length} models)`);
    } else {
      console.log('offline');
    }
  }
  return found;
}

// ---- YAML emit ----------------------------------------------------------

function emitProvider(id, p) {
  let out = `    ${id}:\n`;
  if (p.displayName) out += `      displayName: ${yamlStr(p.displayName)}\n`;
  if (p.apiKeyEnv)   out += `      apiKeyEnv: ${p.apiKeyEnv}\n`;
  if (p.api)         out += `      api: ${p.api}\n`;
  if (p.baseURL)     out += `      baseURL: ${yamlStr(p.baseURL)}\n`;
  if (p.models && p.models.length) {
    out += '      models:\n';
    for (const m of p.models) out += `        - id: ${yamlStr(m.id)}\n`;
  }
  return out;
}

function emitProvidersBlock(providers) {
  let out = 'llm-pi-ai:\n  providers:\n';
  for (const [id, p] of Object.entries(providers)) out += emitProvider(id, p);
  return out;
}

// ---- YAML merge (line-based) --------------------------------------------

// Split a document into top-level sections: each section starts at column 0
// with "key:" and runs to the next column-0 line.
function readTopLevelSections(text) {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  const sections = [];
  let preamble = '';
  let cur = null;
  for (const line of lines) {
    const m = /^([A-Za-z0-9_.-]+):\s*$/.exec(line);
    if (m) {
      if (cur) sections.push(cur);
      cur = { key: m[1], text: line };
    } else if (cur) {
      cur.text += '\n' + line;
    } else {
      preamble += (preamble ? '\n' : '') + line;
    }
  }
  if (cur) sections.push(cur);
  return { sections, preamble };
}

// Parse the providers dict inside an "llm-pi-ai:" section (4-space keys).
// Returns null when the block does not follow the shape we emit, otherwise
// a Map of providerId -> verbatim block text.
function parseProvidersBlock(blockText, baseIndent = 4) {
  const lines = blockText.split('\n');
  const out = new Map();
  let curKey = null;
  let curLines = [];
  let sawProvider = false;
  for (const line of lines) {
    // (?:...) not (...): the group expression must not capture, and `[^:]`
    // would happily match the padding spaces of a structural line such as
    // `  providers:`, turning it into a bogus provider key.
    const m = new RegExp('^ {' + baseIndent + '}([^#\\s][^:\\s][^:]*):\\s*$').exec(line);
    if (m) {
      if (curKey) out.set(curKey, curLines.join('\n'));
      curKey = m[1].trim();
      curLines = [line];
      sawProvider = true;
    } else if (curKey) {
      curLines.push(line);
    } else if (line.trim() !== '') {
      // Before the first provider key we accept exactly two bare headers: the
      // section header at column 0 (`llm-pi-ai:`) and the `providers:` line at
      // one indent level. Anything else means a shape we do not own -> return
      // null so the caller backs up instead of clobbering it.
      // Two bugs lived here: a loose `^\s+providers:` also matches an
      // ordinary provider key, and the key regex `[^:]` happily matched the
      // padding spaces of `  providers:`. Both made the parser treat a
      // structural line as a provider key and drop the whole block.
      const indent = /^ */.exec(line)[0].length;
      if (indent === 0 && /^[A-Za-z0-9_.-]+:\s*$/.test(line)) continue;      // section header
      if (indent === 2 && /^ *providers:\s*$/.test(line)) continue;         // providers: (indent 2 always)
      return null;
    }
  }
  // Trailing blank/comment lines belong to the document, not to the last
  // provider: keep them out so a refresh cannot re-emit a stale marker or a
  // stray comment. A detached comment is dropped (the fresh block carries an
  // accurate one), which keeps repeated runs byte-identical.
  // Flush the last provider: the loop only stores a block when the *next*
  // provider key shows up, so a single-provider block used to parse to an
  // empty Map (and its providers were then dropped on write).
  if (curKey) out.set(curKey, curLines.join('\n'));

  // Normalise every stored block to the 4-space provider depth the merge
  // machinery emits, then drop trailing blank/comment lines.
  const trimmed = [];
  for (const [id, text] of out) {
    const kept = text.split('\n').map((l) => (l.trim() === '' ? '' : ' '.repeat(4 - baseIndent) + l));
    while (kept.length > 1 && /^\s*(#.*)?$/.test(kept[kept.length - 1])) kept.pop();
    trimmed.push([id, kept.join('\n')]);
  }
  const out2 = new Map(trimmed);
  // A block that has provider lines but none parsed -> don't clobber it.
  return out2;
}

// A provider that already exists in settings.yaml but was not reachable this
// run. Its block is reproduced exactly as it was found — only a marker comment
// is added (and refreshed on later runs, never duplicated) so the user can see
// why that endpoint is not answering.
function annotateKeptProvider(id, text) {
  const lines = [];
  for (const line of text.split('\n')) {
    if (/^\s*#\s*probe:\s*unreachable\s*$/.test(line)) continue; // stale marker
    lines.push(line);
  }
  while (lines.length > 1 && lines[lines.length - 1].trim() === '') lines.pop();
  if (!id.startsWith('auto-')) return lines.join('\n');
  return [lines[0], '      # probe: unreachable', ...lines.slice(1)].join('\n');
}

/** Providers declared in a patch entry's `config:` body. The patch may indent
 * its entry with four spaces (as the shipped patches and the web UI do) or
 * with two, so the body is re-normalised to the settings shape first: an
 * absolute-indent parser would see the provider keys at the wrong depth and
 * return an empty map, which silently dropped providers that only exist in the
 * patch. Returns null when the body declares no providers. */
function parsePatchProviders(conceptText) {
  if (!conceptText) return null;
  const lines = conceptText.replace(/\r\n/g, '\n').split('\n');
  let start = -1;
  let bodyIndent = Infinity;
  for (let i = 0; i < lines.length; i++) {
    if (!lines[i].trim()) continue;
    if (start < 0) {
      const key = /^([A-Za-z0-9_.-]+):\s*$/.exec(lines[i].trim());
      if (key && key[1] === 'providers' && !lines[i].startsWith(' ')) { start = i + 1; continue; }
      return null; // first top-level key is not `providers:` -> not our shape
    }
    bodyIndent = Math.min(bodyIndent, /^ */.exec(lines[i])[0].length);
  }
  if (start < 0 || bodyIndent === Infinity) return null;
  // Re-indent to the shape the merge machinery emits and consumes: 4 spaces
  // for a provider key, 6 for its fields (`emitProvider` builds exactly that).
  // Emitting a block that kept its original depth produced invalid YAML —
  // `auto-extra3:` landed beside `providers:` instead of under it.
  const normalized = [`providers:`];
  for (const line of lines.slice(start)) {
    if (!line.trim()) continue;
    normalized.push('  ' + line.slice(bodyIndent));
  }
  return parseProvidersBlock(normalized.join('\n'), 2);
}

/** A comment that only marks a section boundary, e.g. `# --- keep this fence ---`.
 * Ordinary `# note` comments are NOT fences: they stay inside the entry block. */
function isFenceComment(line) {
  const body = line.replace(/^\s*#+\s*/, '').trim();
  return body.endsWith('#') ? isFenceComment(body) : /^[-=*_ ]{3,}$/.test(body);
}

/** Split a profile patch into its comment banner, its `- id:` entry blocks and
 * the comment trailer after the last entry. Each entry keeps its own `- id:`
 * line so a rewrite reproduces the block verbatim. Returns null when the
 * document is not a top-level patch array. */
function readPatchEntries(text) {
  if (!text.trim()) return { header: '', entries: [], trailer: '' };
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  const entries = [];
  let header = '';
  let trailer = '';
  let cur = null;
  let inTrailer = false;
  for (const line of lines) {
    if (!inTrailer) {
      const m = /^-\s+id:\s*([A-Za-z0-9_.-]+)\s*$/.exec(line);
      if (m) {
        cur = { id: m[1], lines: [line] };
        entries.push(cur);
        continue;
      }
      if (!cur && /^-/.test(line)) return null; // not a patch array we understand
    }
    if (cur) {
      // Only a fence comment ends the entry list. Any earlier comment line
      // belongs to the entry block it sits in: treating every comment as the
      // trailer would stop recognising later `- id:` lines and then append a
      // duplicate entry, silently overriding the user's own choice.
      if (isFenceComment(line)) { inTrailer = true; trailer += (trailer ? '\n' : '') + line; }
      else cur.lines.push(line);
      continue;
    }
    header += (header ? '\n' : '') + line;
  }
  return { header: header.replace(/^\n+/, ''), entries, trailer };
}

/** `config:` body of one entry, dedented to the settings-section shape. Footer
 * lines and inline configs come back as their raw text. The body is located by
 * the entry's own indent width — the shipped patches indent entries with four
 * spaces, other authors use two, and a hard-coded assumption silently dropped
 * the whole body (and with it every provider it declared). */
function patchEntryConcept(entry) {
  const lines = entry.lines;
  let idx = -1;
  for (let i = 0; i < lines.length; i++) {
    if (/^ *(?:config):\s*$/.test(lines[i])) { idx = i; break; }
  }
  if (idx < 0) return null;
  const configIndent = /^ */.exec(lines[idx])[0].length;
  let bodyIndent = Infinity;
  for (let i = idx + 1; i < lines.length; i++) {
    if (!lines[i].trim()) continue;
    bodyIndent = Math.min(bodyIndent, /^ */.exec(lines[i])[0].length);
  }
  // Inline body (`config: {...}`) or an empty body: nothing to work with.
  if (bodyIndent === Infinity || bodyIndent <= configIndent) {
    const inline = lines[idx].slice(configIndent + 'config:'.length).trim();
    return inline ? inline + '\n' : null;
  }
  const out = [];
  for (let i = idx + 1; i < lines.length; i++) {
    const line = lines[i];
    if (!line.trim()) { out.push(''); continue; }
    const indent = /^ */.exec(line)[0].length;
    if (indent < bodyIndent) break;
    out.push(line.slice(bodyIndent));
  }
  return out.join('\n').replace(/\s+$/, '\n');
}

/** Dedent every non-empty line by `n` spaces; lines that cannot take it are
 * left alone. Used to move between the 2-space settings shape and the 4-space
 * `config:` body of a patch entry. */
function dedentBy(lines, n) {
  const pad = ' '.repeat(n);
  return lines.map((l) => (l.startsWith(pad) ? l.slice(n) : l));
}

function patchEntryName(entry) {
  for (const line of entry.lines) {
    const m = /^ {2}name:\s*(.+?)\s*$/.exec(line);
    if (m) return m[1];
  }
  return null;
}

/** True when the patch already declares a usable agent-default-model. */
function patchHasModel(parsed, key) {
  const entry = parsed.entries.find((e) => e.id === 'agent-default-model');
  if (!entry) return false;
  for (const line of entry.lines) {
    if (new RegExp('^ {4}' + key + ':\\s*\\S').test(line)) return true;
  }
  return false;
}

/** Entry for `id`, rendered: `- id:` / `  name:` / `  config:` + the merged
 * settings section indented by four spaces. `pkg` may be null when the entry
 * does not need a `name:` (config-only override of a bundle entry). */
function patchEntryBlock(id, pkg, conceptText) {
  let out = `- id: ${id}\n`;
  // Quote the package name: a bare `@scope/name` is invalid YAML (an
  // indicator at the start of a scalar), and the shipped patches quote it too.
  if (pkg) out += `  name: ${yamlStr(pkg)}\n`;
  out += '  config:\n';
  for (const line of conceptText.split('\n')) {
    if (line === '') continue;
    out += '    ' + line + '\n';
  }
  return out;
}

function patchEntryLines(block) {
  return block.replace(/\n$/, '').split('\n');
}

function renderPatchDocument(parsed) {
  const chunks = [];
  const header = (parsed.header || '').replace(/\s+$/, '');
  if (header.trim()) chunks.push(header);
  for (const e of parsed.entries) {
    const chunk = e.lines.join('\n').replace(/\s+$/, '');
    if (chunk.trim()) chunks.push(chunk);
  }
  let doc = chunks.join('\n\n') + '\n';
  const trailer = (parsed.trailer || '').replace(/\s+$/, '');
  if (trailer.trim()) doc += '\n' + trailer + '\n';
  return doc.replace(/\n{3,}/g, '\n\n');
}

/** Replace the `id` entry in place, or append it before the trailing fence. */
function upsertPatchEntry(parsed, id, pkg, conceptText) {
  const block = patchEntryBlock(id, pkg, conceptText);
  const existing = parsed.entries.find((e) => e.id === id);
  if (existing) existing.lines = patchEntryLines(block);
  else parsed.entries.push({ id, lines: patchEntryLines(block) });
}

function mergeSettings(existingText, newProviders, defaultModel, legacyText, patchText) {
  const { sections, preamble } = readTopLevelSections(existingText);

  let llmBlock = null;
  let defaultBlock = null;
  const kept = [];
  for (const s of sections) {
    if (s.key === 'llm-pi-ai') llmBlock = s;
    else if (s.key === 'agent-default-model') defaultBlock = s;
    else kept.push(s.text);
  }

  let body = '';
  if (preamble) body += preamble + '\n';
  for (const t of kept) body += t + '\n';

  // --- agent-default-model: auto-set to first detected model; preserve
  //     user choice if its provider+model still exists in detected set. ---
  if (defaultModel) {
    let useDefault = true;
    if (defaultBlock) {
      // Check if the existing default still points to a valid provider/model
      const mProvider = /^\s*provider:\s*(.+)$/m.exec(defaultBlock.text);
      const mModel    = /^\s*model:\s*(.+)$/m.exec(defaultBlock.text);
      if (mProvider && mModel) {
        const prov = mProvider[1].trim().replace(/^["']|["']$/g, '');
        const mod  = mModel[1].trim().replace(/^["']|["']$/g, '');
        if (newProviders[prov]) {
          const models = (newProviders[prov].models || []).map((m) => m.id);
          if (models.includes(mod)) {
            // Existing default is still valid -> keep it
            body += defaultBlock.text + '\n';
            useDefault = false;
          }
        }
      }
    }
    if (useDefault) {
      body += `agent-default-model:\n  provider: ${defaultModel.provider}\n  model: ${yamlStr(defaultModel.model)}\n\n`;
    }
  } else if (defaultBlock) {
    // No models detected at all -> keep existing default-block as-is
    body += defaultBlock.text + '\n';
  }

  // Providers already declared live in the profile patch, not in this
  // settings-shaped document, so seed from the patch first: a provider whose
  // server is merely down this run must survive as `# probe: unreachable`
  // instead of being dropped. Then fold in a pre-0.2 settings.yaml (or its
  // renamed .imported twin) so the first run after the upgrade carries those
  // over too. Patch wins when both declare the same id.
  const preserved = new Map();
  const seed = (parsed) => { if (parsed) for (const [k, v] of parsed) if (!preserved.has(k)) preserved.set(k, v); };
  const patchParsed = readPatchEntries(patchText);
  if (patchParsed) {
    const entry = patchParsed.entries.find((e) => e.id === 'llm-pi-ai');
    const concept = entry ? patchEntryConcept(entry) : null;
    // Patch config bodies use the entry's own indent width, so they need the
    // relative parser; parseProvidersBlock only understands settings sections.
    if (concept) seed(parsePatchProviders(concept));
  }
  if (legacyText && legacyText.trim()) {
    const legacy = readTopLevelSections(legacyText).sections.find((s) => s.key === 'llm-pi-ai');
    if (legacy) seed(parseProvidersBlock(legacy.text));
  }
  let unexpectedShape = false;
  if (llmBlock) {
    const parsed = parseProvidersBlock(llmBlock.text);
    if (parsed !== null) {
      for (const [k, v] of parsed) preserved.set(k, v);
    } else {
      unexpectedShape = true;
    }
  }

  // Providers are only ever added or refreshed, never dropped: a probe that
  // fails (slow disk, model still loading, server not started yet) must not
  // cost the user a provider that already works. Kept providers keep their
  // position; a kept `auto-*` provider gets a `# probe: unreachable` marker.
  let block = 'llm-pi-ai:\n  providers:\n';
  if (unexpectedShape) {
    // The block has a shape we do not own. Keep a backup and keep the section
    // byte for byte; only append detected providers when the block clearly is
    // a `providers:` mapping and the id is not declared in it already.
    const backup = `${SETTINGS}.auto-backup-${Date.now()}`;
    try { fs.copyFileSync(SETTINGS, backup); } catch {}
    console.log(`  (existing llm-pi-ai block had an unexpected shape; backup kept at ${path.basename(backup)})`);
    block = llmBlock.text.replace(/\s+$/, '') + '\n';
    const extendable = llmBlock.text.split('\n').some((l) => /^  providers:\s*$/.test(l));
    if (extendable) {
      for (const [id, p] of Object.entries(newProviders)) {
        const rx = new RegExp(`^[ ]{2,}${id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}:\\s*$`, 'm');
        if (rx.test(llmBlock.text)) continue;
        block += emitProvider(id, p);
      }
    } else {
      console.log('  (llm-pi-ai is not a `providers:` mapping; detection results were not merged)');
    }
  } else {
    const emitted = new Set();
    for (const [id, v] of preserved) {
      if (newProviders[id]) {
        block += emitProvider(id, newProviders[id]); // refresh in place
        emitted.add(id);
      } else {
        block += annotateKeptProvider(id, v) + '\n'; // keep, marked unreachable
      }
    }
    for (const [id, p] of Object.entries(newProviders)) {
      if (emitted.has(id)) continue; // new this run -> appended
      block += emitProvider(id, p);
    }
  }
  body += block;
  return body;
}

// ---- credentials document (.credentials.yaml) ----------------------------
//
// The document is merged by LINE RANGE and never re-serialized. `records:`
// holds browser-session grants whose values must survive byte for byte, and
// the version of this script that only understood `refs:` dropped the whole
// blob on every launch. Rules:
//   * every top-level section this script does not own (`version`, `records`,
//     anything else) is copied through verbatim,
//   * inside `refs:` only the refs this script owns (`<AUTO_ID>_API_KEY`) are
//     rewritten; every other ref line — including a legacy plaintext
//     DEEPSEEK_API_KEY — is kept exactly as found,
//   * nothing secret is ever written here (see main()).

const CRED_NOTE = '# auto-config: DEEPSEEK_API_KEY is injected into the environment by the launcher '
  + '(DPAPI) and is never persisted here; existing refs are left untouched for the launcher to migrate.';

function isOwnCredentialComment(line) {
  return /^#\s*auto-config:/.test(line);
}

// A `refs:` header carrying a non-empty inline mapping (`refs: { A: 1 }`).
// Its entries cannot be line-merged, and re-emitting the section would drop
// them, so the caller refuses to touch the document instead. `refs: {}` and a
// bare `refs:` are both empty and safe to rebuild.
function refsHeaderIsInlineMapping(lines) {
  const m = /^refs:\s*(.*)$/.exec(lines[0]);
  return m !== null && m[1] !== '' && m[1] !== '{}';
}

// Split into top-level segments: a `key:` at column 0 starts a segment, every
// other line (comments, indented entries, a flow-style `records: { ... }`
// blob) belongs to the current segment. Segments are emitted back in their
// original order, so even a surprising split only reorders identical bytes.
function splitTopLevelSegments(text) {
  const segs = [];
  for (const line of text.replace(/\r\n/g, '\n').split('\n')) {
    const m = /^([A-Za-z0-9_.-]+):/.exec(line);
    if (m) segs.push({ key: m[1], lines: [line] });
    else if (segs.length) segs[segs.length - 1].lines.push(line);
    else segs.push({ key: null, lines: [line] });
  }
  return segs;
}

// The `refs:` mapping, rebuilt from its original lines: entries this script
// owns are dropped and re-emitted with the current value, everything else is
// copied verbatim (no quoting change, no reordering, no deletion).
function renderRefsSection(originalLines, managedRefs) {
  const preserved = [];
  for (const line of originalLines.slice(1)) { // skip the `refs:` header
    if (isOwnCredentialComment(line)) continue; // re-added below, so runs stay idempotent
    const m = /^\s*([A-Za-z_][A-Za-z0-9_]*):\s*(.*)$/.exec(line);
    if (m && Object.prototype.hasOwnProperty.call(managedRefs, m[1])) continue;
    preserved.push(line);
  }
  while (preserved.length && preserved[preserved.length - 1].trim() === '') preserved.pop();
  const out = [CRED_NOTE, 'refs:'];
  for (const line of preserved) out.push(line);
  for (const [k, v] of Object.entries(managedRefs)) out.push(`  ${k}: ${yamlStr(v)}`);
  return out;
}

// Build the next credentials document. Returns null when the existing text is
// a layout this script cannot safely extend; the caller then leaves the file
// completely untouched rather than guessing (a backup is taken first).
function buildCredentialsDocument(existingText, managedRefs) {
  const segs = splitTopLevelSegments(existingText);
  const keys = segs.filter((s) => s.key !== null).map((s) => s.key);
  const versioned = keys.includes('version') || keys.includes('refs') || keys.includes('records');

  if (!versioned && existingText.trim() !== '') {
    // Pre-release flat layout (`KEY: value` at column 0). Recognized exactly
    // as the credentials provider recognizes it, and upgraded the same way:
    // the original lines are nested under `refs:` verbatim. An upgrade keeps
    // every value; guessing at anything else would not.
    const flat = existingText.replace(/\r\n/g, '\n').split('\n').filter((l) => l.trim() !== '');
    const recognizable = flat.every((l) => /^[A-Za-z_][A-Za-z0-9_]*:\s*\S/.test(l));
    if (!recognizable) return null;
    // Nest the original lines under `refs:` — through the same renderer, so a
    // ref this script owns is replaced, never duplicated (a duplicate key
    // would make the document unparseable for the strict credentials reader).
    const nested = ['refs:'];
    for (const line of flat) nested.push(`  ${line}`);
    const out = ['version: 1'];
    for (const line of renderRefsSection(nested, managedRefs)) out.push(line);
    out.push('records: {}');
    return out.join('\n') + '\n';
  }

  const blocks = [];
  let preamble = [];
  for (const seg of segs) {
    const lines = seg.lines.filter((l) => !isOwnCredentialComment(l));
    if (seg.key === null) preamble = lines;
    else if (seg.key === 'refs') {
      if (refsHeaderIsInlineMapping(seg.lines)) return null; // cannot be line-merged
      blocks.push({ key: 'refs', lines: renderRefsSection(seg.lines, managedRefs) });
    } else blocks.push({ key: seg.key, lines }); // version / records / anything else: verbatim
  }
  if (!blocks.some((b) => b.key === 'version')) blocks.unshift({ key: 'version', lines: ['version: 1'] });
  if (!blocks.some((b) => b.key === 'refs')) {
    const at = blocks.findIndex((b) => b.key === 'version');
    blocks.splice(at + 1, 0, { key: 'refs', lines: renderRefsSection(['refs:'], managedRefs) });
  }
  if (!blocks.some((b) => b.key === 'records')) blocks.push({ key: 'records', lines: ['records: {}'] });

  const out = preamble.filter((l) => l.trim() !== '');
  for (const b of blocks) for (const l of b.lines) out.push(l);
  return out.join('\n').replace(/\n+$/, '') + '\n';
}

// ---- main ----------------------------------------------------------------

async function main() {
  console.log(`DSH_HOME: ${DSH_HOME}`);
  fs.mkdirSync(DSH_HOME, { recursive: true });

  const found = await detect();
  console.log('');

  const providers = {};
  const apiKeyRefs = {}; // ref name -> placeholder value (written to .credentials.yaml)
  for (const b of found) {
    // Local OpenAI-compatible endpoints need a credential reference: pi-ai's
    // openai-completions protocol refuses to issue a request without an API
    // key, so every auto-detected provider gets a placeholder ref that the
    // local llama-server / Ollama ignore anyway (Authorization header).
    const ref = `${b.id.toUpperCase()}_API_KEY`.replace(/-/g, '_');
    providers[b.id] = {
      displayName: b.name,
      api: 'openai-completions',
      baseURL: b.base + b.v1,
      apiKeyEnv: ref,
      models: b.models.map((id) => ({ id })),
    };
    apiKeyRefs[ref] = 'local';
  }

  const apiKey = (process.env.DEEPSEEK_API_KEY || '').trim();
  if (apiKey) {
    // pi-ai catalog route "deepseek" (model catalog comes from the installed
    // catalog; the key is resolved via the credential/env mechanism, where the
    // inherited environment wins over the file). The value itself is NOT
    // persisted anywhere: the launcher already holds it DPAPI-encrypted and
    // injects it into this process's environment.
    providers.deepseek = { apiKeyEnv: 'DEEPSEEK_API_KEY' };
    console.log('  DeepSeek API key: provided via environment (not persisted)');
  }

  // Write the credentials document (version-1 layout: `version: 1` + `refs:`).
  // Only refs this script owns are written; every other ref — including a
  // legacy plaintext DEEPSEEK_API_KEY that an earlier build stored — and the
  // whole `records:` blob are preserved verbatim.
  if (Object.keys(apiKeyRefs).length > 0) {
    const existing = fs.existsSync(CREDENTIALS) ? fs.readFileSync(CREDENTIALS, 'utf8') : '';
    if (existing.trim() !== '' && !/^version:\s*1\s*$/m.test(existing)) {
      const backup = `${CREDENTIALS}.auto-backup-${Date.now()}`;
      try { fs.copyFileSync(CREDENTIALS, backup); } catch {}
      console.log(`  (credentials document was not version 1; backup kept at ${path.basename(backup)})`);
    }
    const next = buildCredentialsDocument(existing, apiKeyRefs);
    if (next === null) {
      console.log('  (credentials document has a layout this script does not understand; left untouched)');
    } else if (next === existing) {
      console.log(`Credentials already up to date: ${CREDENTIALS}`);
    } else {
      writeFileAtomic(CREDENTIALS, next, 0o600);
      console.log(`Credentials written: ${CREDENTIALS}`);
    }
  }

  // Live config lives in the profile patch from dsh 0.2 on. Read it when it
  // exists; also fold in a pre-0.2 settings.yaml / settings.yaml.imported once
  // so providers a previous version wrote there survive the upgrade.
  const patchText = fs.existsSync(PATCH) ? fs.readFileSync(PATCH, 'utf8') : '';
  let legacyText = '';
  for (const candidate of [SETTINGS, SETTINGS + '.imported']) {
    if (fs.existsSync(candidate)) { legacyText = fs.readFileSync(candidate, 'utf8'); break; }
  }

  // Determine default model: first auto-detected provider with at least one
  // model, preferring the one declared in DSH_EXTRA_ENDPOINTS (auto-extra1).
  let defaultModel = null;
  const providerOrder = Object.keys(providers);
  // Prefer auto-extra1 (user-declared endpoint) first, then others
  const preferred = providerOrder.sort((a, b) => {
    if (a === 'auto-extra1') return -1;
    if (b === 'auto-extra1') return 1;
    return 0;
  });
  for (const id of preferred) {
    const p = providers[id];
    if (p.models && p.models.length > 0) {
      defaultModel = { provider: id, model: p.models[0].id };
      break;
    }
  }
  if (defaultModel) {
    console.log(`Default model: ${defaultModel.provider} / ${defaultModel.model}`);
  }

  const merged = mergeSettings(patchText, providers, defaultModel, legacyText, patchText);

  const parsedPatch = readPatchEntries(patchText);
  if (parsedPatch === null) {
    console.log('  (profile patch is not a top-level list of `- id:` entries; left untouched)');
  } else {
    const parsedMerged = readTopLevelSections(merged).sections;
    const bodyOf = (key) => {
      const sec = parsedMerged.find((s) => s.key === key);
      if (!sec) return null;
      const bodyLines = sec.text.split('\n').slice(1);
      return dedentBy(bodyLines, 2).join('\n').replace(/\s+$/, '\n');
    };
    const llmBody = bodyOf('llm-pi-ai');
    if (llmBody) upsertPatchEntry(parsedPatch, 'llm-pi-ai', "@deepseek-ai/dsh-llm-pi-ai", llmBody);
    else console.log('  (llm-pi-ai section missing from the merged document; providers not written)');
    // Only supply a fallback default model when the profile has none: the
    // patch shipped with the deployment usually picks deepseek-official, and
    // auto-detection must not silently override a deliberate choice.
    if (defaultModel && !patchHasModel(parsedPatch, 'model')) {
      const defBody = bodyOf('agent-default-model');
      if (defBody) {
        upsertPatchEntry(parsedPatch, 'agent-default-model', "@deepseek-ai/dsh-agent-default-model", defBody);
        console.log(`  Default model fallback written: ${defaultModel.provider} / ${defaultModel.model}`);
      }
    }
    fs.mkdirSync(PROFILE_DIR, { recursive: true });
    if (patchText.trim()) {
      const backup = `${PATCH}.auto-backup-${Date.now()}`;
      try { fs.copyFileSync(PATCH, backup); } catch {}
    }
    writeFileAtomic(PATCH, renderPatchDocument(parsedPatch).replace(/\s+$/, '\n'), 0o644);
  }

  // Summary
  const keys = Object.keys(providers);
  if (keys.length) {
    console.log('Configured providers:');
    for (const id of keys) {
      const p = providers[id];
      const what = p.models ? p.models.map((m) => m.id).join(', ') : p.apiKeyEnv || '';
      console.log(`  - ${id}  ${p.baseURL || p.apiKeyEnv || ''}  [${what || 'catalog'}]`);
    }
  } else {
    console.log('No local model server detected. Start Ollama / LM Studio / llama.cpp / vLLM,');
    console.log('then run start.bat again. (DeepSeek API works via DEEPSEEK_API_KEY.)');
  }
  console.log(`Profile patch written: ${PATCH}`);
}

main().catch((e) => {
  console.error('auto-config error:', e);
  process.exit(1);
});
