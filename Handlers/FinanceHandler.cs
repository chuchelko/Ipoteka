using StackExchange.Redis;
using System.Globalization;
using System.Text.Json;
using System.Linq;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Ipoteka.Models;

namespace Ipoteka.Handlers;

public class FinanceHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly string[] _finToken;

    public FinanceHandler(ITelegramBotClient botClient, string[] finToken)
    {
        _botClient = botClient;
        _finToken = finToken;
    }

    private static DateTime GetCurrentTimeUtc3()
    {
        return TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"));
    }

    public async Task HandleFinAuth(long userId, string text, IDatabase redis)
    {
        var parts = text.Split(' ');
        if (parts.Length < 2)
        {
            await _botClient.SendMessage(userId, "❌ Укажите токен. Пример: /fin_auth ваш_токен");
            return;
        }

        if (_finToken.Contains(text.Split(' ')[1].Trim()))
        {
            await redis.SetAddAsync(UtilityKeys.FinAuthUsersKey(), userId);
            await _botClient.SendMessage(userId, "✅ Вы получили доступ к финансовому учету");
        }
        else
        {
            await _botClient.SendMessage(userId, "❌ Неверный токен");
        }
    }

    public async Task HandleFinAddCategory(long userId, string text, IDatabase redis)
    {
        if (!await redis.SetContainsAsync(UtilityKeys.FinAuthUsersKey(), userId))
        {
            await _botClient.SendMessage(userId, "❌ Нет доступа. Сначала авторизуйтесь /fin_auth [токен]");
            return;
        }

        var parts = text.Split(' ', 2);
        if (parts.Length < 2)
        {
            await _botClient.SendMessage(userId, "❌ Укажите категорию. Пример: /fin_add_category Еда");
            return;
        }

        var category = parts[1].Trim();

        // Проверяем, существует ли уже такая категория
        if (await redis.SetContainsAsync(UtilityKeys.FinCategoriesKey(userId), category))
        {
            await _botClient.SendMessage(userId, $"❌ Категория «{category}» уже существует");
            return;
        }

        // Сохраняем категорию в список категорий
        await redis.SetAddAsync(UtilityKeys.FinCategoriesKey(userId), category);

        // Создаем временную запись категории для планируемого расхода
        var tempCategoryKey = $"temp_category:{userId}:{category}";
        await redis.StringSetAsync(tempCategoryKey, category);

        await _botClient.SendMessage(userId,
            $"✅ Категория «{category}» добавлена!\n\n💰 Теперь укажите планируемый расход на эту категорию в месяц (например: 15000):");
    }

    public async Task HandleFinListCategories(long userId, IDatabase redis)
    {
        var categories = await redis.SetMembersAsync(UtilityKeys.FinCategoriesKey(userId));
        if (categories.Length == 0)
        {
            await _botClient.SendMessage(userId, "📂 Категорий пока нет. Добавьте через /fin_add_category");
            return;
        }

        var list = string.Join("\n", categories.Select(c => $"• {c}"));
        await _botClient.SendMessage(userId, $"📂 Ваши категории:\n{list}");
    }

    public async Task HandleFinSetBudget(long userId, string text, IDatabase redis)
    {
        var parts = text.Split(' ');
        if (parts.Length < 3 ||
            !decimal.TryParse(parts[2], CultureInfo.InvariantCulture, out var limit))
        {
            await _botClient.SendMessage(userId, "❌ Формат: /fin_set_budget 2025-10 50000");
            return;
        }

        var month = parts[1].Trim();
        var data = new { Limit = limit, Spent = 0m };
        await redis.StringSetAsync(UtilityKeys.FinBudgetKey(userId, month), JsonSerializer.Serialize(data));

        await _botClient.SendMessage(userId, $"✅ Бюджет на {month} установлен: {limit}₽");
    }

    public async Task HandleFinAddExpense(long userId, IDatabase redis)
    {
        if (!await redis.SetContainsAsync(UtilityKeys.FinAuthUsersKey(), userId))
        {
            await _botClient.SendMessage(userId, "❌ Нет доступа. Авторизуйтесь /fin_auth [токен]");
            return;
        }

        var categories = await redis.SetMembersAsync(UtilityKeys.FinCategoriesKey(userId));
        if (categories.Length == 0)
        {
            await _botClient.SendMessage(userId, "📂 Сначала добавьте категории через /fin_add_category");
            return;
        }

        // Создаем кнопки с категориями
        var buttons = categories.Select(category => InlineKeyboardButton.WithCallbackData(
            category.ToString(), $"expense_category:{category}:{userId}")).ToList();

        // Разбиваем на ряды по 2 кнопки
        var rows = new List<IEnumerable<InlineKeyboardButton>>();
        for (int i = 0; i < buttons.Count; i += 2)
        {
            rows.Add(buttons.Skip(i).Take(2));
        }

        var inlineKeyboard = new InlineKeyboardMarkup(rows);

        await _botClient.SendMessage(userId,
            "💰 Выберите категорию для расхода:",
            replyMarkup: inlineKeyboard);
    }

    public async Task HandleFinAnalytics(long userId, string text, IDatabase redis)
    {
        var parts = text.Split(' ', 2);
        var monthParam = parts.Length > 1 ? parts[1] : null;

        if (monthParam == null)
        {
            // Показываем кнопки для выбора месяца
            await ShowMonthSelection(userId, redis);
            return;
        }

        var month = monthParam;
        await ShowExpenseAnalytics(userId, month, redis);
    }

    private async Task ShowMonthSelection(long userId, IDatabase redis)
    {
        var currentMonth = GetCurrentTimeUtc3();
        var months = new List<string>();

        // Добавляем текущий месяц и 5 предыдущих
        for (int i = 0; i < 6; i++)
        {
            var monthDate = currentMonth.AddMonths(-i);
            months.Add(monthDate.ToString("yyyy-MM"));
        }

        var buttons = months.Select(month => InlineKeyboardButton.WithCallbackData(
            $"{month} ({GetMonthName(month)})",
            $"analytics_month:{month}:{userId}")).ToList();

        // Разбиваем на ряды по 2 кнопки
        var rows = new List<IEnumerable<InlineKeyboardButton>>();
        for (int i = 0; i < buttons.Count; i += 2)
        {
            rows.Add(buttons.Skip(i).Take(2));
        }

        var inlineKeyboard = new InlineKeyboardMarkup(rows);

        await _botClient.SendMessage(userId,
            "📊 Выберите месяц для аналитики:",
            replyMarkup: inlineKeyboard);
    }

    public async Task ShowExpenseAnalytics(long userId, string month, IDatabase redis)
    {
        var key = UtilityKeys.FinExpensesKey(userId, month);
        var list = await redis.ListRangeAsync(key);
        if (list.Length == 0)
        {
            await _botClient.SendMessage(userId, $"📭 Нет трат за {month}");
            return;
        }

        var expenses = list
            .Select(j => JsonSerializer.Deserialize<ExpenseRecord>(j!)!)
            .GroupBy(e => e.Category)
            .Select(g => {
                dynamic dynamicExpense = new { Category = g.Key, Sum = g.Sum(x => x.Amount), Planned = GetPlannedAmount(userId, g.Key, redis) };
                return dynamicExpense;
                })
            .ToList();

        if (expenses.Count == 0)
        {
            await _botClient.SendMessage(userId, $"📭 Нет данных за {month}");
            return;
        }

        var totalSpent = expenses.Sum(x => x.Sum);
        var totalPlanned = expenses.Sum(x => x.Planned);

        var analyticsText = $"📊 Аналитика за {month} ({GetMonthName(month)}):\n\n";

        // Добавляем данные по каждой категории
        foreach (var expense in expenses.OrderByDescending(x => x.Sum))
        {
            double percentage = expense.Planned > 0 ? (double)(expense.Sum / expense.Planned * 100) : 0;
            var status = GetStatusEmoji(percentage);

            analyticsText += $"{status} {expense.Category}:\n";
            analyticsText += $"  💰 Факт: {expense.Sum}₽\n";
            analyticsText += $"  📋 План: {expense.Planned}₽\n";
            analyticsText += $"  📈 Выполнение: {percentage:F1}%\n";
            analyticsText += $"  {GetProgressBar(percentage)}\n\n";
        }

        analyticsText += $"💰 Общий фактический расход: {totalSpent}₽\n";
        analyticsText += $"📋 Общий планируемый расход: {totalPlanned}₽\n";
        analyticsText += $"📈 Общее выполнение плана: {(totalPlanned > 0 ? (totalSpent / totalPlanned * 100) : 0)}:F1%\n";

        // Создаем текстовый график
        analyticsText += $"\n📈 График расходов:\n{GenerateTextChart(expenses)}";

        await _botClient.SendMessage(userId, analyticsText);
    }

    private static decimal GetPlannedAmount(long userId, string category, IDatabase redis)
    {
        var categoryKey = UtilityKeys.FinCategoryDataKey(userId, category);
        var categoryJson = redis.StringGet(categoryKey);
        if (categoryJson.IsNullOrEmpty) return 0;

        var categoryData = JsonSerializer.Deserialize<CategoryRecord>(categoryJson!);
        return categoryData?.PlannedAmount ?? 0;
    }

    private static string GetMonthName(string month)
    {
        var months = new[]
        {
            "Январь", "Февраль", "Март", "Апрель", "Май", "Июнь",
            "Июль", "Август", "Сентябрь", "Октябрь", "Ноябрь", "Декабрь"
        };

        if (DateTime.TryParse($"{month}-01", out var date))
        {
            return $"{months[date.Month - 1]} {date.Year}";
        }

        return month;
    }

    private static string GetStatusEmoji(double percentage)
    {
        if (percentage <= 50) return "🟢";
        if (percentage <= 80) return "🟡";
        if (percentage <= 100) return "🟠";
        return "🔴";
    }

    private static string GetProgressBar(double percentage)
    {
        var filled = (int)Math.Round(percentage / 10);
        var empty = 10 - filled;

        return $"[{"".PadRight(filled, '█')}{"".PadRight(empty, '░')}] {percentage:F0}%";
    }

    private static string GenerateTextChart(List<dynamic> expenses)
    {
        if (expenses.Count == 0) return "";

        var maxAmount = expenses.Max(x => x.Sum);
        var chart = "";

        foreach (var expense in expenses.OrderByDescending(x => x.Sum))
        {
            var barLength = maxAmount > 0 ? (int)((expense.Sum / maxAmount) * 20) : 0;
            var bar = "".PadRight(barLength, '█');
            chart += $"{expense.Category,-15} {bar} {expense.Sum,6:F0}₽\n";
        }

        return chart;
    }

    public async Task HandleExpenseHistory(long userId, string text, IDatabase redis)
    {
        var parts = text.Split(' ', 2);
        var month = parts.Length > 1 ? parts[1] : DateTime.UtcNow.ToString("yyyy-MM");

        var key = UtilityKeys.FinExpensesKey(userId, month);
        var expenses = await redis.ListRangeAsync(key);

        if (expenses.Length == 0)
        {
            await _botClient.SendMessage(userId, $"📭 Нет расходов за {month}");
            return;
        }

        // Преобразуем расходы в список
        var expenseList = expenses
            .Select((json, index) => (JsonSerializer.Deserialize<ExpenseRecord>(json!)!, index))
            .ToList();

        // Показываем первую страницу (первые 5 расходов)
        await ShowExpenseHistoryPage(userId, expenseList, month, 0, redis);
    }

    private async Task ShowExpenseHistoryPage(long userId, List<(ExpenseRecord expense, int index)> expenses, string month, int page, IDatabase redis)
    {
        const int itemsPerPage = 5;
        var totalPages = (int)Math.Ceiling(expenses.Count / (double)itemsPerPage);
        var startIndex = page * itemsPerPage;
        var endIndex = Math.Min(startIndex + itemsPerPage, expenses.Count);

        var pageExpenses = expenses.Skip(startIndex).Take(itemsPerPage).ToList();

        var response = $"📜 Расходы за {month} (страница {page + 1}/{totalPages}):\n\n";

        for (int i = 0; i < pageExpenses.Count; i++)
        {
            var (expense, originalIndex) = pageExpenses[i];
            var expenseNumber = startIndex + i + 1;
            response += $"{expenseNumber}. {expense.Category}: {expense.Amount}₽ - {expense.Date:dd.MM.yyyy HH:mm}\n";
        }

        // Создаем кнопки для навигации и действий
        var buttons = new List<InlineKeyboardButton[]>();

        // Кнопки навигации
        var navButtons = new List<InlineKeyboardButton>();
        if (page > 0)
            navButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"expense_history_page:{month}:{page - 1}:{userId}"));

        if (page < totalPages - 1)
            navButtons.Add(InlineKeyboardButton.WithCallbackData("Вперед ➡️", $"expense_history_page:{month}:{page + 1}:{userId}"));

        if (navButtons.Count > 0)
            buttons.Add(navButtons.ToArray());

        // Кнопки действий для каждого расхода
        for (int i = 0; i < pageExpenses.Count; i++)
        {
            var (expense, originalIndex) = pageExpenses[i];
            var expenseNumber = startIndex + i + 1;
            var actionButtons = new[]
            {
                InlineKeyboardButton.WithCallbackData($"✏️ Изменить {expenseNumber}", $"expense_edit:{month}:{originalIndex}:{userId}"),
                InlineKeyboardButton.WithCallbackData($"🗑️ Удалить {expenseNumber}", $"expense_delete:{month}:{originalIndex}:{userId}")
            };
            buttons.Add(actionButtons);
        }

        var inlineKeyboard = new InlineKeyboardMarkup(buttons);

        await _botClient.SendMessage(userId, response, replyMarkup: inlineKeyboard);
    }

    public async Task HandleExpenseCallback(long userId, string callbackData, IDatabase redis)
    {
        var parts = callbackData.Split(':');
        if (parts.Length < 3 || parts[0] != "expense_category") return;

        var category = parts[1];
        var originalUserId = long.Parse(parts[2]);

        // Проверяем, что callback от того же пользователя
        if (originalUserId != userId) return;

        // Проверяем авторизацию пользователя
        if (!await redis.SetContainsAsync(UtilityKeys.FinAuthUsersKey(), userId))
        {
            await _botClient.AnswerCallbackQuery(callbackData, "❌ Нет доступа к финансовому учету");
            return;
        }

        // Создаем кнопки для ввода суммы
        var amountButtons = new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("💰 Ввести сумму", $"expense_amount:{category}:{userId}") },
            new[] { InlineKeyboardButton.WithCallbackData("❌ Отмена", $"expense_cancel:{userId}") }
        };

        var keyboard = new InlineKeyboardMarkup(amountButtons);

        await _botClient.SendMessage(userId,
            $"✅ Выбрана категория: {category}\n\n💬 Теперь введите сумму расхода (например: 1500)",
            replyMarkup: keyboard);

        await _botClient.AnswerCallbackQuery(callbackData, $"Выбрана категория: {category}", showAlert: false);
    }

    public async Task HandleExpenseAmount(long userId, string messageText, IDatabase redis)
    {
        // Проверяем авторизацию пользователя
        if (!await redis.SetContainsAsync(UtilityKeys.FinAuthUsersKey(), userId))
        {
            await _botClient.SendMessage(userId, "❌ Нет доступа к финансовому учету");
            return;
        }

        if (!decimal.TryParse(messageText.Replace(" ", "").Replace(",", "."), NumberStyles.Currency, CultureInfo.InvariantCulture, out var amount))
        {
            await _botClient.SendMessage(userId, "❌ Неверный формат суммы. Введите число (например: 1500)");
            return;
        }

        if (amount <= 0)
        {
            await _botClient.SendMessage(userId, "❌ Сумма должна быть больше нуля");
            return;
        }

        // Получаем выбранную категорию из временного хранилища
        var selectedCategoryKey = $"expense_temp_category:{userId}";
        var category = await redis.StringGetAsync(selectedCategoryKey);

        if (category.IsNullOrEmpty)
        {
            await _botClient.SendMessage(userId, "❌ Сначала выберите категорию через /fin_add_expense");
            return;
        }

        // Создаем запись о расходе
        var expense = new ExpenseRecord
        {
            UserId = userId,
            Category = category.ToString(),
            Amount = amount,
            Description = $"Расход в категории {category}",
            Date = GetCurrentTimeUtc3()
        };

        // Сохраняем расход
        var currentMonth = GetCurrentTimeUtc3().ToString("yyyy-MM");
        await redis.ListRightPushAsync(UtilityKeys.FinExpensesKey(userId, currentMonth),
            JsonSerializer.Serialize(expense));

        // Очищаем временную категорию
        await redis.KeyDeleteAsync(selectedCategoryKey);

        await _botClient.SendMessage(userId,
            $"✅ Расход добавлен!\n📂 Категория: {category}\n💰 Сумма: {amount}₽\n📅 Дата: {expense.Date:dd.MM.yyyy HH:mm}",
            replyMarkup: new ReplyKeyboardRemove());

        // Показываем обновленную аналитику
        await HandleFinAnalytics(userId, $"/fin_analytics {currentMonth}", redis);
    }

    public async Task HandleCategoryPlannedAmount(long userId, string messageText, IDatabase redis)
    {
        // Проверяем авторизацию пользователя
        if (!await redis.SetContainsAsync(UtilityKeys.FinAuthUsersKey(), userId))
        {
            await _botClient.SendMessage(userId, "❌ Нет доступа к финансовому учету");
            return;
        }

        if (!decimal.TryParse(messageText.Replace(" ", "").Replace(",", "."), NumberStyles.Currency, CultureInfo.InvariantCulture, out var plannedAmount))
        {
            await _botClient.SendMessage(userId, "❌ Неверный формат суммы. Введите число (например: 15000)");
            return;
        }

        if (plannedAmount < 0)
        {
            await _botClient.SendMessage(userId, "❌ Сумма не может быть отрицательной");
            return;
        }

        // Ищем временную категорию
        var pattern = $"temp_category:{userId}:*";
        var tempKeys = GetKeysByPattern(redis, pattern);

        if (tempKeys.Length == 0)
        {
            await _botClient.SendMessage(userId, "❌ Сначала добавьте категорию через /fin_add_category");
            return;
        }

        // Берем первую найденную временную категорию
        var tempCategoryKey = tempKeys[0];
        var category = await redis.StringGetAsync(tempCategoryKey);

        if (category.IsNullOrEmpty)
        {
            await _botClient.SendMessage(userId, "❌ Ошибка при получении категории");
            return;
        }

        // Создаем запись категории с планируемым расходом
        var categoryRecord = new CategoryRecord
        {
            UserId = userId,
            Name = category.ToString(),
            PlannedAmount = plannedAmount,
            CreatedAt = GetCurrentTimeUtc3()
        };

        // Сохраняем данные категории
        await redis.StringSetAsync(UtilityKeys.FinCategoryDataKey(userId, category.ToString()),
            JsonSerializer.Serialize(categoryRecord));

        // Удаляем временную категорию
        await redis.KeyDeleteAsync(tempCategoryKey);

        await _botClient.SendMessage(userId,
            $"✅ Планируемый расход установлен!\n📂 Категория: {category}\n💰 Планируемый расход: {plannedAmount}₽ в месяц",
            replyMarkup: new ReplyKeyboardRemove());
    }

    private string[] GetKeysByPattern(IDatabase redis, string pattern)
    {
        var server = redis.Multiplexer.GetServer(redis.Multiplexer.GetEndPoints()[0]);
        var keys = server.Keys(pattern: pattern).ToArray();
        return keys.Select(k => k.ToString()).ToArray();
    }

    public async Task HandleExpenseHistoryCallback(long userId, string callbackData, IDatabase redis)
    {
        var parts = callbackData.Split(':');
        if (parts.Length < 4) return;

        var action = parts[0];
        var month = parts[1];
        var originalUserId = long.Parse(parts[3]);

        // Проверяем, что callback от того же пользователя
        if (originalUserId != userId) return;

        // Проверяем авторизацию пользователя
        if (!await redis.SetContainsAsync(UtilityKeys.FinAuthUsersKey(), userId))
        {
            await _botClient.AnswerCallbackQuery(callbackData, "❌ Нет доступа к финансовому учету");
            return;
        }

        switch (action)
        {
            case "expense_history_page":
                // Для навигации по страницам используем parts[2] как номер страницы
                if (int.TryParse(parts[2], out var page))
                {
                    await ShowExpenseHistoryPage(userId, month, page, redis);
                }
                break;

            case "expense_edit":
            case "expense_delete":
            case "expense_delete_confirm":
                // Для операций с расходами используем parts[2] как индекс расхода
                if (int.TryParse(parts[2], out var expenseIndex))
                {
                    switch (action)
                    {
                        case "expense_edit":
                            await StartExpenseEdit(userId, month, expenseIndex, redis);
                            break;
                        case "expense_delete":
                            await ConfirmExpenseDelete(userId, month, expenseIndex, redis);
                            break;
                        case "expense_delete_confirm":
                            await DeleteExpense(userId, month, expenseIndex, redis);
                            break;
                    }
                }
                break;

            case "expense_delete_cancel":
                await _botClient.AnswerCallbackQuery(callbackData, "Удаление отменено");
                await ShowExpenseHistoryPage(userId, month, 0, redis);
                break;
        }

        await _botClient.AnswerCallbackQuery(callbackData, null, showAlert: false);
    }

    private async Task ShowExpenseHistoryPage(long userId, string month, int page, IDatabase redis)
    {
        var key = UtilityKeys.FinExpensesKey(userId, month);
        var expenses = await redis.ListRangeAsync(key);

        if (expenses.Length == 0)
        {
            await _botClient.SendMessage(userId, $"📭 Нет расходов за {month}");
            return;
        }

        // Преобразуем расходы в список
        var expenseList = expenses
            .Select((json, index) => (JsonSerializer.Deserialize<ExpenseRecord>(json!)!, index))
            .ToList();

        await ShowExpenseHistoryPage(userId, expenseList, month, page, redis);
    }

    private async Task StartExpenseEdit(long userId, string month, int expenseIndex, IDatabase redis)
    {
        var key = UtilityKeys.FinExpensesKey(userId, month);
        var expenses = await redis.ListRangeAsync(key);

        if (expenseIndex < 0 || expenseIndex >= expenses.Length)
        {
            await _botClient.SendMessage(userId, "❌ Расход не найден");
            return;
        }

        var expense = JsonSerializer.Deserialize<ExpenseRecord>(expenses[expenseIndex]!)!;

        // Сохраняем индекс редактируемого расхода
        var editKey = $"expense_edit:{userId}:{month}:{expenseIndex}";
        await redis.StringSetAsync(editKey, JsonSerializer.Serialize(expense));

        var editButtons = new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("✏️ Изменить сумму", $"expense_edit_amount:{month}:{expenseIndex}:{userId}") },
            new[] { InlineKeyboardButton.WithCallbackData("📝 Изменить категорию", $"expense_edit_category:{month}:{expenseIndex}:{userId}") },
            new[] { InlineKeyboardButton.WithCallbackData("❌ Отмена", $"expense_edit_cancel:{month}:{expenseIndex}:{userId}") }
        };

        var keyboard = new InlineKeyboardMarkup(editButtons);

        await _botClient.SendMessage(userId,
            $"✏️ Редактирование расхода:\n\n📂 Категория: {expense.Category}\n💰 Сумма: {expense.Amount}₽\n📅 Дата: {expense.Date:dd.MM.yyyy HH:mm}\n\nВыберите, что изменить:",
            replyMarkup: keyboard);
    }

    private async Task ConfirmExpenseDelete(long userId, string month, int expenseIndex, IDatabase redis)
    {
        var key = UtilityKeys.FinExpensesKey(userId, month);
        var expenses = await redis.ListRangeAsync(key);

        if (expenseIndex < 0 || expenseIndex >= expenses.Length)
        {
            await _botClient.SendMessage(userId, "❌ Расход не найден");
            return;
        }

        var expense = JsonSerializer.Deserialize<ExpenseRecord>(expenses[expenseIndex]!)!;

        var confirmButtons = new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("✅ Да, удалить", $"expense_delete_confirm:{month}:{expenseIndex}:{userId}") },
            new[] { InlineKeyboardButton.WithCallbackData("❌ Нет, отменить", $"expense_delete_cancel:{month}:{expenseIndex}:{userId}") }
        };

        var keyboard = new InlineKeyboardMarkup(confirmButtons);

        await _botClient.SendMessage(userId,
            $"🗑️ Удалить расход?\n\n📂 Категория: {expense.Category}\n💰 Сумма: {expense.Amount}₽\n📅 Дата: {expense.Date:dd.MM.yyyy HH:mm}",
            replyMarkup: keyboard);
    }

    private async Task DeleteExpense(long userId, string month, int expenseIndex, IDatabase redis)
    {
        var key = UtilityKeys.FinExpensesKey(userId, month);
        var expenses = await redis.ListRangeAsync(key);

        if (expenseIndex < 0 || expenseIndex >= expenses.Length)
        {
            await _botClient.SendMessage(userId, "❌ Расход не найден");
            return;
        }

        // Удаляем расход из списка
        await redis.ListSetByIndexAsync(key, expenseIndex, RedisValue.Null);
        await redis.ListTrimAsync(key, 0, expenses.Length - 2);

        await _botClient.SendMessage(userId, "✅ Расход удален!");

        // Показываем обновленную историю
        await ShowExpenseHistoryPage(userId, month, 0, redis);
    }
}
