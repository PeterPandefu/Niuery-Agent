const { _electron: electron } = require('playwright');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const assert = require('node:assert/strict');
(async()=>{
 const data=fs.mkdtempSync(path.join(os.tmpdir(),'niuery-desktop-'));
 fs.mkdirSync(path.join(data,'.git'));
 const app=await electron.launch({args:[path.join(__dirname,'..')],env:{...process.env,NIUERY_TEST_DATA:data}});
 try{
  const page=await app.firstWindow();const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.getByRole('heading',{name:'开始一个新任务'}).waitFor();
  assert.equal(await page.getByRole('button',{name:'新建任务',exact:true}).count(),0);
  await page.getByRole('button',{name:'新建 Chat 任务',exact:true}).click();
  await page.getByRole('heading',{name:'新的 Chat 对话'}).waitFor();
  assert((await page.getByText('新的 Chat 对话',{exact:true}).count()) >= 1);
  assert.equal(await page.locator('select[aria-label="Chat 临时项目"]').count(),0);
  assert.equal(await page.locator('select[aria-label="模型"]').count(),1);
  if (!fs.existsSync(path.resolve(__dirname,'../../../config/providers.local.json'))) {
   assert.equal(await page.getByRole('button',{name:'新建任务',exact:true}).count(),0);
   assert.equal(await page.getByRole('button',{name:'添加项目',exact:true}).count(),1);
   assert.equal(await page.getByRole('button',{name:'新建 Chat 任务',exact:true}).count(),1);
   assert.equal(await page.locator('.starter-grid button').count(),3);
   assert.equal(await page.getByRole('button',{name:'模型服务',exact:true}).count(),1);
   assert.equal(errors.length,0,errors.join('\n'));
   console.log('桌面静态交互验收通过（未配置 providers.local.json，跳过真实模型调用）。');
   return;
  }
  await page.locator('select[aria-label="模型"] option').first().waitFor({state:'attached'});
  await page.evaluate(directory=>localStorage.setItem('workspace',directory),data);
  await page.reload();
  await page.getByRole('heading',{name:'开始一个新任务'}).waitFor();
  await page.getByRole('button',{name:/移除项目/}).first().click();
  await page.getByRole('dialog',{name:'移除项目？'}).waitFor();
  await page.getByRole('button',{name:'取消',exact:true}).click();
  await page.locator('.starter-grid button').first().click();
  assert.equal(await page.getByRole('textbox',{name:'任务说明'}).inputValue(),'解释项目结构');
  await page.getByRole('textbox',{name:'任务说明'}).fill('请只回复：桌面验收成功。不要调用工具。');
  await page.getByRole('button',{name:'开始执行',exact:true}).click();
  await page.getByRole('heading',{name:'请只回复：桌面验收成功。不要调用工具。'}).waitFor();
  await page.locator('.status-chip').filter({hasText:'已完成'}).waitFor({timeout:180000});
  assert((await page.locator('.answer').innerText()).includes('桌面验收成功'));
  await page.getByRole('button',{name:'活动',exact:true}).click();
  await page.locator('.activity-list').waitFor();
  const taskRow = page.locator('.task-row').filter({hasText:'请只回复：桌面验收成功。不要调用工具。'}).first();
  await taskRow.click({button:'right'});
  await page.getByRole('button',{name:'删除任务',exact:true}).click();
  await taskRow.waitFor({state:'detached'});
  assert.equal(errors.length,0,errors.join('\n'));
  const output=path.resolve(__dirname,'../../../artifacts');fs.mkdirSync(output,{recursive:true});
  const capture=await Promise.race([app.evaluate(async({BrowserWindow})=>(await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64')),new Promise((_,reject)=>setTimeout(()=>reject(new Error('截图未在十秒内完成。')),10000))]);
  fs.writeFileSync(path.join(output,'stage2-desktop.png'),Buffer.from(capture,'base64'));
  fs.writeFileSync(path.join(output,'stage2-desktop.json'),JSON.stringify({status:'通过',checks:['真实 Electron 启动','选择目录','任务发送','真实流式结果','活动视图','无页面异常']},null,2));
  console.log('桌面真实交互验收通过。');
 }finally{await app.close();}
})().catch(e=>{console.error('桌面验收失败：'+e.message);process.exitCode=1;});
