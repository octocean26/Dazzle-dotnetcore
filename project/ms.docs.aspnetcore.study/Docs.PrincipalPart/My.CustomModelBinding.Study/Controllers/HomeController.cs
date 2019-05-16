﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using My.CustomModelBinding.Study.Data;

namespace My.CustomModelBinding.Study.Controllers
{
    public class HomeController:Controller
    {
        private readonly IHostingEnvironment _env;

        public HomeController(IHostingEnvironment env){
            _env = env;
        }
        public IActionResult Index(){
            return View();
        }
        
        [HttpPost]
        public void PostFile(byte[] file,string filename){
            string filePath = Path.Combine(_env.ContentRootPath, "wwwroot/images/upload", filename);
            if (System.IO.File.Exists(filePath)) return;
            System.IO.File.WriteAllBytes(filePath, file);
        }

        [HttpGet("get/{authorId}")]
        public IActionResult Get(Student author){
            return Ok(author);
        }
    }
}
