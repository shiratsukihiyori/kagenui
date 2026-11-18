import { readFileSync } from "node:fs";
import { createHash, createPublicKey, verify } from "node:crypto";

// 校验 Kagenui 发布包的 Ed25519 签名
// 用法: node verify-exe.mjs kagenui.exe

const PUB = "vffi1ubRKXdHI510Goe+7ZjYzZiWK2Kczt6Q5ARaBDo=";
const file = process.argv[2] ?? "kagenui.exe";

const key = createPublicKey({
  key: Buffer.concat([Buffer.from("302a300506032b6570032100", "hex"), Buffer.from(PUB, "base64")]),
  format: "der",
  type: "spki",
});

const buf = readFileSync(file);
const sig = Buffer.from(readFileSync(file + ".sig", "utf8").trim(), "base64");
const ok = verify(null, buf, key, sig);

console.log("文件   :", file);
console.log("大小   :", (buf.length / 1048576).toFixed(2), "MB");
console.log("sha256 :", createHash("sha256").update(buf).digest("hex"));
console.log("签名   :", ok ? "验证通过 ✅ 来自 白月日和 的签名" : "验证失败 ❌ 文件被改过或签名不匹配");
process.exitCode = ok ? 0 : 1;
