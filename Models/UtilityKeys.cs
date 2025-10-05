namespace Ipoteka.Models;

public static class UtilityKeys
{
    public static string CreditKey() => "credit:global";
    public static string HistoryKey() => "history:global";
    public static string AuthChatsKey() => "auth:chats";
    public static string AuthUsersKey() => "auth:users";
    public static string FinAuthUsersKey() => "fin:auth_users";
    public static string FinCategoriesKey(long userId) => $"fin:categories:{userId}";
    public static string FinCategoryDataKey(long userId, string category) => $"fin:category:{userId}:{category}";
    public static string FinBudgetKey(long userId, string month) => $"fin:budget:{userId}:{month}";
    public static string FinExpensesKey(long userId, string month) => $"fin:expenses:{userId}:{month}";
}
