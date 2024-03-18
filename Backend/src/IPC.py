import clr

clr.AddReference('System.Core')
exec("import System")
exec("from System.IO.Pipes import NamedPipeClientStream")

class IPC:
    def __init__(self, pipe_name='MucNamedPipe'):
        self.pipe_name = pipe_name
        self.client = None

    def send(self, message):
        try:
            self.connect()
            self.send_to_unity(message)
            return self.receive_from_unity()
        finally:
            if self.client is not None:
                self.client.Close()

    def connect(self):
        self.client = exec("NamedPipeClientStream(self.pipe_name)")
        self.client.Connect(1000)

    def send_to_unity(self, message):
        if self.client is None:
            raise Exception('Client is not connected')
        bytes = exec("System.Text.Encoding.UTF8.GetBytes(message)")
        self.client.Write(bytes, 0, len(bytes))
        self.client.Flush()

    def receive_from_unity(self):
        if self.client is None:
            raise Exception('Client is not connected')
        buffer = exec("System.Array.CreateInstance(System.Byte, 1024)")
        len = self.client.Read(buffer, 0, buffer.Length)
        return exec("System.Text.Encoding.UTF8.GetString(buffer, 0, len)")