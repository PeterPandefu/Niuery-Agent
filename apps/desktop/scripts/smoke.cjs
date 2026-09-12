const { _electron: electron } = require('playwright');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const assert = require('node:assert/strict');
(async()=>{
 const data=fs.mkdtempSync(path.join(os.tmpdir(),'niuery-desktop-'));
 const app=await electron.launch({args:[path.join(__dirname,'..')],env:{...process.env,NIUERY_TEST_DATA:data}});
 try{
  const page=await app.firstWindow();const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.getByRole('heading',{name:'开始一个新任务'}).waitFor();
  await page.getByRole('option',{name:'gpt-4.1',exact:true}).waitFor({state:'attached'});
  await app.evaluate(({dialog},directory)=>{dialog.showOpenDialog=async()=>({canceled:false,filePaths:[directory]});},data);
  await page.getByRole('button',{name:'选择项目',exact:true}).click();
  await page.getByRole('button',{name:'解释这个项目的结构',exact:true}).click();
  assert.equal(await page.getByRole('textbox',{name:'任务说明'}).inputValue(),'解释这个项目的结构');
  await page.getByRole('textbox',{name:'任务说明'}).fill('请只回复：桌面验收成功。不要调用工具。');
  await page.getByRole('button',{name:'开始执行',exact:true}).click();
  await page.getByRole('heading',{name:'任务详情'}).waitFor();
  await page.locator('.run-label').filter({hasText:'已完成'}).waitFor({timeout:180000});
  assert((await page.locator('.answer').innerText()).includes('桌面验收成功'));
  await page.getByRole('button',{name:'活动',exact:true}).click();
  await page.locator('.activity').waitFor();
  assert.equal(errors.length,0,errors.join('\n'));
  const output=path.resolve(__dirname,'../../../artifacts');fs.mkdirSync(output,{recursive:true});
  const capture=await Promise.race([app.evaluate(async({BrowserWindow})=>(await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64')),new Promise((_,reject)=>setTimeout(()=>reject(new Error('截图未在十秒内完成。')),10000))]);
  fs.writeFileSync(path.join(output,'stage2-desktop.png'),Buffer.from(capture,'base64'));
  fs.writeFileSync(path.join(output,'stage2-desktop.json'),JSON.stringify({status:'通过',checks:['真实 Electron 启动','选择目录','任务发送','真实流式结果','活动视图','无页面异常']},null,2));
  console.log('桌面真实交互验收通过。');
 }finally{await app.close();}
})().catch(e=>{console.error('桌面验收失败：'+e.message);process.exitCode=1;});
