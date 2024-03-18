import lodestone, json

class Player:
    def __init__(self, ip, port, username, auth):
        self.player = lodestone.createBot(
            host = ip,
            port= port,
            username = username,
            auth = auth,
            ls_disable_viewer= True
        )

    #Envio
    def Query(self):
        query = {
            "health": self.player.health(),
            "food": self.player.food(),
            "foodSaturation": self.player.food_saturation(),
            "rain": self.player.is_raining(),
            "oxygen": self.player.oxygen_level()
        }
        return json.dumps(query)

    #Recibido
    def Chat(self, message): self.player.chat(message)
    """
    def GetInput(self, walk, jump, camera):
        if walk[0] < .3 or walk[1] < .3 or walk[0] > -.3 or walk[1] > -.3: self.sneak = True
        else: self.sneak = False

        if walk[0] > .1: self.walkX = [True, False]
        elif walk[0] < -.1: self.walkX = [False, True]
        else: self.walkX = [False, False]

        if walk[1] > .1: self.walkY = [True, False]
        elif walk[1] < -.1: self.walkY = [False, True]
        else: self.walkY = [False, False]

        self.bot.set_control_state("sneak", self.sneak)
        self.bot.set_control_state("jump", jump)

        self.bot.set_control_state("forward", self.walkX[0])
        self.bot.set_control_state("backward", self.walkX[1])
        self.bot.set_control_state("left", self.walkY[0])
        self.bot.set_control_state("right", self.walkY[1])
    """