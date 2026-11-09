import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, '..');

const sourceDir = path.join(repoRoot, 'env', 'data');
const outputDir = path.join(repoRoot, 'env', 'data-prepared');

if (!fs.existsSync(sourceDir)) {
    console.warn(`[Corpus Normalizer] Source directory '${sourceDir}' not found. Skipping.`);
    process.exit(0);
}

console.log(`\n====> [Corpus Normalizer] Scanning raw text files in '${sourceDir}'...`);

const smokeDir = path.join(outputDir, 'smoke');
const coreDir = path.join(outputDir, 'core');
const fullDir = path.join(outputDir, 'full');

fs.mkdirSync(smokeDir, { recursive: true });
fs.mkdirSync(coreDir, { recursive: true });
fs.mkdirSync(fullDir, { recursive: true });

// Helper to recursively collect files
function collectFiles(dir) {
    const entries = fs.readdirSync(dir, { withFileTypes: true });
    let files = [];
    for (const entry of entries) {
        const fullPath = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            if (entry.name !== 'data-prepared' && entry.name !== 'smoke' && entry.name !== 'core' && entry.name !== 'full') {
                files = files.concat(collectFiles(fullPath));
            }
        } else if (entry.isFile() && /\.txt$/i.test(entry.name)) {
            files.push(fullPath);
        }
    }
    return files;
}

const rawFiles = collectFiles(sourceDir);
console.log(`Found ${rawFiles.length} raw novel files.`);

function cleanNovelTitle(fileName) {
    let clean = fileName.replace(/【[^】]*】/g, '');
    clean = clean.replace(/\([^)]*cunlove[^)]*\)/gi, '');
    clean = clean.replace(/\.txt$/i, '');
    return clean.trim();
}

function decodeText(buffer) {
    // Check BOM
    if (buffer.length >= 3 && buffer[0] === 0xEF && buffer[1] === 0xBB && buffer[2] === 0xBF) {
        return buffer.slice(3).toString('utf8');
    }
    // Attempt strict UTF-8
    try {
        const utf8Decoder = new TextDecoder('utf-8', { fatal: true });
        return utf8Decoder.decode(buffer);
    } catch {
        // Fallback to GB18030 / GBK
        const gbkDecoder = new TextDecoder('gb18030');
        return gbkDecoder.decode(buffer);
    }
}

function formatMarkdown(rawText, title, edition) {
    const lines = rawText.split(/\r?\n/);
    const output = [];
    output.push(`# ${title} (${edition})`);
    output.push(`<!-- metadata: title=${title}; edition=${edition}; source=ReferenceRAG-JinYong-Corpus -->\n`);

    for (const rawLine of lines) {
        const line = rawLine.trim();
        if (!line) continue;
        if (line.includes('cunlove.cn') || line.includes('免费分享') || line.includes('耗时整理')) {
            continue; // strip ad watermarks
        }

        // Match Chinese chapter headings: 第X回 / 第一章 / 回目
        if (/^第[一二三四五六七八九十百]+[回卷章]\s*(.*)$/.test(line)) {
            output.push(`\n## ${line}\n`);
        } else {
            output.push(line);
            output.push('');
        }
    }
    return output.join('\n');
}

let count = 0;
for (const filePath of rawFiles) {
    const buffer = fs.readFileSync(filePath);
    const decoded = decodeText(buffer);

    // Get relative path within the source folder to determine edition
    const relPath = path.relative(sourceDir, filePath);
    let edition = '三联版';
    if (relPath.includes(path.sep + '新修版') || relPath.startsWith('新修版')) {
        edition = '新修版';
    } else if (relPath.includes(path.sep + '旧版') || relPath.startsWith('旧版')) {
        edition = '旧版';
    } else if (relPath.includes(path.sep + '三联版') || relPath.startsWith('三联版')) {
        edition = '三联版';
    }

    const baseName = path.basename(filePath);
    const cleanTitle = cleanNovelTitle(baseName);
    const cleanFileName = `${cleanTitle}.md`;

    const mdContent = formatMarkdown(decoded, cleanTitle, edition);

    // 1. Output to full tier (subdivided by edition)
    const editionDir = path.join(fullDir, edition);
    fs.mkdirSync(editionDir, { recursive: true });
    const fullPath = path.join(editionDir, cleanFileName);
    fs.writeFileSync(fullPath, mdContent, 'utf8');

    // 2. Output to smoke tier (越女剑)
    if (cleanTitle.includes('越女剑') && edition === '三联版') {
        const smokePath = path.join(smokeDir, cleanFileName);
        fs.writeFileSync(smokePath, mdContent, 'utf8');
        console.log(` [Smoke Tier Generated] ${cleanFileName} (${(fs.statSync(smokePath).size / 1024).toFixed(1)} KB)`);
    }

    // 3. Output to core tier (连城诀, 雪山飞狐)
    if ((cleanTitle.includes('连城诀') || cleanTitle.includes('雪山飞狐')) && edition === '三联版' && !cleanTitle.includes('旧版')) {
        const corePath = path.join(coreDir, cleanFileName);
        fs.writeFileSync(corePath, mdContent, 'utf8');
        console.log(` [Core Tier Generated] ${cleanFileName} (${(fs.statSync(corePath).size / 1024).toFixed(1)} KB)`);
    }

    count++;
}

console.log(`\n [SUCCESS] Successfully transcoded, cleaned, and partitioned ${count} novels into '${outputDir}'!`);
