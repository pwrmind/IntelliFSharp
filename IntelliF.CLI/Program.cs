using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static Process StartFSI()
    {
        var fsiProc = new Process();
        fsiProc.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "fsi",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        fsiProc.Start();

        // Переместите сюда
        fsiProc.BeginOutputReadLine();
        fsiProc.BeginErrorReadLine();

        return fsiProc;
    }

    static async Task<(string stdout, string stderr)> ExecuteFsiCodeAsync(Process fsiProc, string code)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        using var outputCompleted = new ManualResetEvent(false);

        DataReceivedEventHandler outputHandler = (sender, e) => {
            if (e.Data != null)
            {
                if (e.Data.StartsWith("> "))
                {
                    outputCompleted.Set();
                }
                else
                {
                    outputBuilder.AppendLine(e.Data);
                }
            }
        };

        DataReceivedEventHandler errorHandler = (sender, e) => {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        fsiProc.StandardInput.WriteLine("#reset;;");
        await Task.Delay(500); // Дать время на сброс

        fsiProc.OutputDataReceived += outputHandler;
        fsiProc.ErrorDataReceived += errorHandler;

        // Уберите этот вызов
        // fsiProc.BeginOutputReadLine();

        await fsiProc.StandardInput.WriteLineAsync(code);
        await fsiProc.StandardInput.FlushAsync();

        bool completed = outputCompleted.WaitOne(10000);
        fsiProc.OutputDataReceived -= outputHandler;
        fsiProc.ErrorDataReceived -= errorHandler;

        if (!completed)
        {
            errorBuilder.AppendLine("🕒 Таймаут при выполнении кода");
        }

        return (outputBuilder.ToString().Trim(), errorBuilder.ToString().Trim());
    }

    static List<string> ExtractFSharpCode(string llmResponse)
    {
        var codeBlocks = new List<string>();
        var matches = Regex.Matches(
            llmResponse,
            @"```(?:fsharp)?\s*(.*?)```",
            RegexOptions.Singleline
        );

        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                string code = match.Groups[1].Value.Trim();
                // Удаляем BOM (UTF-8 signature)
                if (!string.IsNullOrEmpty(code) && code[0] == '\uFEFF')
                {
                    code = code.Substring(1);
                }
                codeBlocks.Add(code);
            }
        }
        return codeBlocks;
    }

    static async Task<string> SendToOllamaAsync(string model, string prompt)
    {
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(120);

        var requestData = new
        {
            model,
            prompt,
            stream = false
        };

        var json = JsonSerializer.Serialize(requestData);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(
            "http://localhost:11434/api/generate",
            content
        );

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"🔥 Ошибка запроса: {response.StatusCode}\n{await response.Content.ReadAsStringAsync()}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(responseContent);
        return jsonDoc.RootElement.GetProperty("response").GetString();
    }

    static async Task Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        using var fsi = StartFSI();
        const string modelName = "deepseek-coder-v2:latest";
        const int maxAttempts = 3;
        int attempt = 0;
        bool success = false;
        string finalResponse = "";

        // Шаг 1: Системное сообщение
        var systemMsg = "🚀 Ты — F# эксперт. Весь код в ```fsharp...``` будет выполнен в FSI. " +
                        "Ты будешь получать результаты выполнения команд написанных тобой " +
                        "сообщениями после твоих ответов. Всегда отвечай с кодом в блоке fsharp.";

        Console.WriteLine($"📩 Отправляем системное сообщение:\n{systemMsg}");
        await SendToOllamaAsync(modelName, systemMsg);
        Console.WriteLine("✅ Системное сообщение отправлено\n");

        // Шаг 2: Отправка задания
        var taskMsg = "📝 Задание: Напиши F#-функцию, вычисляющую сумму квадратов чисел от 1 до n. " +
                      "Не включай примеры использования в код, только саму функцию.";

        Console.WriteLine($"📤 Отправляем задание:\n{taskMsg}");
        var llmResponse = await SendToOllamaAsync(modelName, taskMsg);
        Console.WriteLine($"📥 Получен ответ от LLM:\n{llmResponse}\n");

        // Цикл выполнения с обратной связью
        while (attempt < maxAttempts && !success)
        {
            attempt++;
            Console.WriteLine($"🔄 Попытка #{attempt}");

            // Шаг 3-4: Извлечение и выполнение кода
            var codeBlocks = ExtractFSharpCode(llmResponse);
            Console.WriteLine($"🔍 Извлечено блоков кода: {codeBlocks.Count}");

            for (var i = 0; i < codeBlocks.Count; i++)
            {
                Console.WriteLine($"📦 Блок #{i + 1}:\n{codeBlocks[i]}\n");
                Console.WriteLine("══════════════════════");
            }

            if (codeBlocks.Count == 0)
            {
                Console.WriteLine("⚠️ Не найдено исполняемых блоков кода");
                break;
            }

            var results = new List<(string stdout, string stderr)>();
            for (var i = 0; i < codeBlocks.Count; i++)
            {
                Console.WriteLine($"⚡ Выполняем блок #{i + 1}...");
                var result = await ExecuteFsiCodeAsync(fsi, codeBlocks[i] + ";;");

                Console.WriteLine($"📊 Результат выполнения блока #{i + 1}:");
                Console.WriteLine($"✅ stdout: {result.stdout}");
                Console.WriteLine($"❗ stderr: {result.stderr}");
                Console.WriteLine("══════════════════════");

                results.Add(result);
            }

            // Анализ результатов
            bool hasErrors = false;
            foreach (var result in results)
            {
                if (!string.IsNullOrEmpty(result.stderr))
                {
                    hasErrors = true;
                    break;
                }
            }

            if (!hasErrors)
            {
                success = true;
                finalResponse = "🎉 Код успешно выполнен без ошибок!";
                Console.WriteLine(finalResponse);
                break;
            }

            // Шаг 5: Отправка результатов обратно для исправления
            var resultPrompt = new StringBuilder();
            resultPrompt.AppendLine("🛠 Обнаружены ошибки при выполнении кода:");
            for (int i = 0; i < results.Count; i++)
            {
                resultPrompt.AppendLine($"📦 Результат выполнения блока #{i + 1}:");
                resultPrompt.AppendLine($"✅ stdout: {results[i].stdout}");
                resultPrompt.AppendLine($"❗ stderr: {results[i].stderr}");
            }
            resultPrompt.AppendLine("🚫 Исправь код и отправь только исправленную версию в блоке fsharp.");

            Console.WriteLine($"📤 Отправляем результаты выполнения в LLM:\n{resultPrompt}");
            llmResponse = await SendToOllamaAsync(modelName, resultPrompt.ToString());
            Console.WriteLine($"📥 Получен исправленный ответ от LLM:\n{llmResponse}\n");
        }

        if (!success)
        {
            finalResponse = "🚫 Достигнуто максимальное количество попыток. Завершение.";
            Console.WriteLine(finalResponse);
        }
    }
}