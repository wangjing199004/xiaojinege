# 内置模板保护回归

在工作区根目录执行：
`dotnet run --project tests/HpTemplateProtection/HpTemplateProtection.csproj -c Release`

直接编译生产模板读取代码及同一组加密资源。覆盖全部7个内置标准模板，验证解密结果与源模板逐字节一致、重复打开独立临时文件、资源头非明文DWG、篡改/截断拒绝，以及正常和异常路径的临时文件清理。
模板采用AES-256-CBC及独立HMAC-SHA256密钥，先认证后解密；此检查不声称能阻止本地逆向。
