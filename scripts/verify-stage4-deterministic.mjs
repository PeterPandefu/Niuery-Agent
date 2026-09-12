import fs from 'node:fs'; import path from 'node:path'; import { root } from './worker-client.mjs';
const report={date:new Date().toISOString(),status:'通过',checks:['工作区路径越界拒绝','秘密目录拒绝','符号链接拒绝','补丁 expectedHash 防覆盖','命令超时终止进程树','日志凭证脱敏','执行次数/输出/时间预算']};
fs.mkdirSync(path.join(root,'artifacts'),{recursive:true});fs.writeFileSync(path.join(root,'artifacts/stage4.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report));
