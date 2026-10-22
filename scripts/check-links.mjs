// Link + asset checker for the repo docs.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = process.argv[2]
const files = ['README.md', 'docs/浣跨敤璇存槑.md', 'docs/鍏充簬.txt', 'LICENSE']

const mdLink = /\[([^\]]*)\]\(([^)]+)\)/g
const htmlSrc = /(?:src|href)="([^"]+)"/g

const problems = []
const externals = new Set()

for (const rel of files) {
  const abs = path.join(root, rel)
  if (!fs.existsSync(abs)) { problems.push(`${rel}: 鏂囦欢涓嶅瓨鍦╜); continue }
  const text = fs.readFileSync(abs, 'utf8')
  for (const re of [mdLink, htmlSrc]) {
    re.lastIndex = 0
    let m
    while ((m = re.exec(text)) !== null) {
      const target = (re === mdLink ? m[2] : m[1]).trim()
      if (!target || target.startsWith('#')) {
        // 椤靛唴閿氱偣锛氭鏌ユ爣棰樻槸鍚﹀瓨鍦?        if (target.startsWith('#')) {
          const anchor = target.slice(1)
          const headings = text.match(/^#{1,6}\s+(.+)$/gm) || []
          const slug = (s) => s.trim().replace(/[銆侊紝銆傦細:?!锛?锛堬級()銆愩€慭[\]鈥溾€?']/g, '').replace(/\s+/g, '-').toLowerCase()
          const match = headings.some((h) => slug(h.replace(/^#+\s+/, '')) === anchor.toLowerCase())
          if (!match) problems.push(`${rel}: 椤靛唴閿氱偣鎵句笉鍒板搴旀爣棰?鈫?${target}`)
        }
        continue
      }
      if (/^(https?:)?\/\//.test(target) || target.startsWith('mailto:')) { externals.add(target); continue }
      const clean = target.split('#')[0]
      const file = path.join(root, clean)
      if (!fs.existsSync(file)) problems.push(`${rel}: 鐩稿閾炬帴鎸囧悜涓嶅瓨鍦ㄧ殑鏂囦欢 鈫?${target}`)
    }
  }
}

console.log('=== 鐩稿閾炬帴 / 璧勬簮 ===')
console.log(problems.length ? problems.map((p) => '  [FAIL] ' + p).join('\n') : '  鍏ㄩ儴鏈夋晥')

console.log('\n=== 澶栭摼锛堥渶鑱旂綉楠岃瘉锛?==')
for (const u of externals) console.log('  ' + u)
