import {request} from 'playwright';
import fs from 'node:fs';
const r=await request.newContext({baseURL:'http://127.0.0.1:3871'});
const auth={email:process.env.TEST_ADMIN_EMAIL,password:process.env.TEST_ADMIN_PASSWORD};
for(const p of ['/api/auth/admin','/api/auth/login']){const x=await r.post(p,{data:auth});console.log(p,x.status());if(!x.ok())throw Error(await x.text());}
for(const [p,data] of [['/api/teams',{name:'Demo Team',agent:'echo',memberAgents:['echo'],testerAgents:['echo']}],['/api/teams/DemoTeam/sites',{name:'team-dashboard'}],['/api/teams/DemoTeam/sites',{name:'unpublished'}]]){const x=await r.post(p,{data});console.log(p,x.status());if(!x.ok())console.log(await x.text());}
const folder='/tmp/lior-w27510/dr/documents/DemoTeam/sitesrc';fs.mkdirSync(folder,{recursive:true});fs.writeFileSync(folder+'/index.html','<!doctype html><title>Lior independent verification</title><h1>Independent W27510 site</h1><p>Published from c07ddbe verification fixture.</p>');
const x=await r.post('/api/teams/DemoTeam/sites/team-dashboard/publish',{data:{folder}});console.log('publish',x.status(),await x.text());await r.dispose();
