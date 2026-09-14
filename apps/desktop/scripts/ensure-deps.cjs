const { existsSync } = require('node:fs');
const { spawnSync } = require('node:child_process');
const { join } = require('node:path');

const root = join(__dirname, '..');
if (existsSync(join(root, 'node_modules', 'typescript', 'bin', 'tsc'))) process.exit(0);

const result = spawnSync('npm', ['ci'], { cwd: root, stdio: 'inherit', shell: true });
process.exit(result.status ?? 1);
