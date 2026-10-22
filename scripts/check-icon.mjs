// Verify the icon payloads landed inside the compiled exe.
import fs from 'node:fs'

const exe = fs.readFileSync(process.argv[2])
const ico = fs.readFileSync(process.argv[3])

console.log('exe bytes:', exe.length)
console.log('contains resource name "whale.ico":', exe.includes(Buffer.from('whale.ico')))

const count = ico.readUInt16LE(4)
let total = 0
for (let i = 0; i < count; i++) {
  const o = 6 + i * 16
  const len = ico.readUInt32LE(o + 8)
  const off = ico.readUInt32LE(o + 12)
  const fp = ico.subarray(off, off + 64)
  let hits = 0
  let at = 0
  while ((at = exe.indexOf(fp, at)) !== -1) { hits++; at += 1 }
  const dim = ico[o] || 256
  total += hits
  console.log(`  ${String(dim).padStart(3)}px payload found ${hits}x in exe`)
}
console.log('total occurrences:', total, '(>=2 per size = Win32 icon resource + embedded resource)')
