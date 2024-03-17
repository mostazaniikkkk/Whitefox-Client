using System;
using System.Runtime.InteropServices;

public class Connection{
    // Declaraci�n de las funciones de la biblioteca de C++
    [DllImport("botcraft.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Connect(string address, string login);

    [DllImport("botcraft.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Disconnect();

    [DllImport("botcraft.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SendChatMessage(string message);

    static void Main(string[] args){
        try{
            // Conexi�n al servidor
            Connect("127.0.0.1:25565", "BCHelloWorld");

            // Espera de 5 segundos
            System.Threading.Thread.Sleep(5000);

            // Env�o del mensaje
            SendChatMessage("Hello, World!");

            // Espera de 5 segundos
            System.Threading.Thread.Sleep(5000);

            // Desconexi�n del servidor
            Disconnect();
        }
        catch (Exception e){
            Console.WriteLine("Exception: " + e.Message);
        }
    }
}