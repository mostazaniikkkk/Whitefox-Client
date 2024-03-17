using UnityEngine;

public abstract class Mob : MonoBehaviour {
    public string name;
    public int life;

    Transform mob;
    private void Start(){
        mob = GetComponent<Transform>();
        if (name is not null){
            //Aqui deberia cargar la etiqueta
        }
    }

    private void Update(){
        
    }
}