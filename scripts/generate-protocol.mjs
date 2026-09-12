import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const schema=JSON.parse(fs.readFileSync(path.join(root,'packages/protocol/schema.json'),'utf8'));
const methods=schema.properties.method.enum;
const outputs={
  'apps/desktop/src/protocol.generated.ts': `// 根据协议定义生成，请运行 node scripts/generate-protocol.mjs 更新。\nexport type Method = ${methods.map(JSON.stringify).join(' | ')};\nexport interface Request { version: 1; requestId: string; method: Method; payload: Record<string, unknown> }\n`,
  'src/Niuery.Agent.Worker/Protocol.generated.cs': `// 根据协议定义生成，请运行 node scripts/generate-protocol.mjs 更新。\nusing System.Text.Json;\nnamespace Niuery.Agent.Worker;\npublic sealed record Request(int Version, string RequestId, string Method, JsonElement Payload);\npublic static class Protocol { public static readonly string[] Methods = [${methods.map(JSON.stringify).join(', ')}]; }\n`
};
for(const [file,content] of Object.entries(outputs)){
 const target=path.join(root,file);
 if(process.argv.includes('--check')){if(!fs.existsSync(target)||fs.readFileSync(target,'utf8')!==content)throw new Error('协议生成文件需要更新：'+file);}
 else fs.writeFileSync(target,content);
}
console.log('协议类型一致性检查完成。');
